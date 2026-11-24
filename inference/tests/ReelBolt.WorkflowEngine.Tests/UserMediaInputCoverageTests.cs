#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Editing;
using ReelBolt.WorkflowEngine.Services.Billing;
using ReelBolt.WorkflowEngine.Services.Review;
using ReelBolt.WorkflowEngine.Services.Segmentation;
using ReelBolt.WorkflowEngine.Services.Video;
using ReelBolt.WorkflowEngine.Services.Video.TimelineRender;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Security ENG-3, review fixes: the drift guard over the engine's ffmpeg argv builders, the staging
/// extension at every Analyze/Compile/TimelineRender staging site, the pinned whitelist and refused set,
/// and the inputs outside the three steps (review facts, segmentation vendor copy, stills).
/// </summary>
public class UserMediaInputCoverageTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "eng3c-" + Guid.NewGuid().ToString("N"));

    public UserMediaInputCoverageTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Drift guard: every -i in the user-media argv builders carries the options
    // ---------------------------------------------------------------------------------------------

    private static string EngineSourceDir()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "inference", "ReelBolt.sln")))
            dir = Path.GetDirectoryName(dir);
        dir.Should().NotBeNull("the test runs inside the repository");
        return Path.Combine(dir!, "inference", "src", "ReelBolt.WorkflowEngine");
    }

    /// <summary>
    /// Every <c>-i</c> outside the user-media guard, keyed by file and the input expression after <c>-i</c>,
    /// with the exact number of sites each covers, so a new site that reuses a name still fails. Three kinds:
    /// engine-authored inputs (which must NOT carry the user-media options), inputs guarded by another family's
    /// own whitelist, and inputs outside ENG-3's scope that a follow-up must review (marked FOLLOW-UP). An input
    /// forced to <c>-f lavfi</c> or <c>-f concat</c> is exempt by rule (synthesised in-graph or the engine's own
    /// list; neither demuxer is on the whitelist, so the options would break it).
    /// </summary>
    private static readonly (string File, string Input, int Sites, string Why)[] Exempt =
    [
        // Engine-authored inputs.
        ("Execution/StepExecutors/VideoCompileStepExecutor.cs", "policy.WatermarkAssetPath!", 2, "the operator's own watermark raster"),
        ("Agents/Tools/ReactRemotionSandboxTools.cs", "policy.WatermarkAssetPath!", 1, "the operator's own watermark raster"),
        ("Services/Segmentation/SegmentationMaskArgvBuilder.cs", "framePattern", 1, "the engine's own rendered mask frames, -f image2"),
        ("Services/Video/TimelineRender/AudioMixBuilder.cs", "mixPath", 3, "the engine's own mixed WAV (measure, silence check, normalize)"),
        ("Services/Video/TimelineRender/TimelineRenderer.cs", "programVideoPath", 1, "the engine's own concatenated program video"),
        ("Services/Video/TimelineRender/TimelineRenderer.cs", "finalAudioPath", 2, "the engine's own normalized audio"),
        ("Services/Compute/OutputVerifier.cs", "url", 1, "a worker's output, probed with the verifier's own protocol and format whitelists"),
        // Guarded by another family's own whitelist.
        ("Services/Video/FfmpegArgvBuilder.cs", "inputPath", 1, DERIV),
        ("Services/Segmentation/SegmentationMaskPreviewArgvBuilder.cs", "maskPath", 1, DERIV),
        ("Services/Marketplace/MarketplacePreviewArgvBuilder.cs", "inputPath", 1, DERIV),
        ("Services/Marketplace/MarketplacePreviewArgvBuilder.cs", "previewPath", 1, DERIV),
        ("Services/Video/FilmstripArgvBuilder.cs", "inputPath", 1, DERIV),
        ("Services/Video/MediaDerivativeService.cs", "inputPath", 1, DERIV),
        ("Services/Video/ProxyArgvBuilder.cs", "inputPath", 1, DERIV),
        ("Services/Video/StabilizedProxyArgvBuilder.cs", "inputPath", 2, DERIV),
        ("Services/Video/StagedVideoProxy.cs", "inputPath", 1, DERIV),
        ("Services/Video/Events/FrameSignalSampler.cs", "inputPath", 1, DERIV),
        ("Services/Video/Events/VideoEventDetector.cs", "inputPath", 1, DERIV),
        ("Services/Video/Cleanup/AudioCleanupArgv.cs", "inputPath", 1, DERIV),
        ("Services/Video/Cleanup/AudioCleanupArgv.cs", "videoPath", 1, DERIV),
        ("Services/Video/Cleanup/AudioCleanupArgv.cs", "stemPath", 1, "the cleanup stem: its own StemFormatWhitelist"),
        ("Services/Video/Cleanup/AudioCleanupArgv.cs", "cleanWavPath", 1, "the engine's cleaned WAV: -format_whitelist wav"),
        ("Services/Video/Cleanup/LocalAudioCleaner.cs", "sourcePath", 1, DERIV),
        ("Services/Video/Cleanup/MainsFrequencyProbe.cs", "sourcePath", 1, DERIV),
        ("Services/Avatars/AvatarStillThenAnimate.cs", "input", 2, "the avatar picture: its own protocol and picture-format whitelists"),
        ("Services/Video/Enhance/EnhanceBoundedCopy.cs", "inputPath", 1, ENH),
        ("Services/Video/Enhance/EnhanceDuration.cs", "inputPath", 2, ENH),
        ("Services/Video/Enhance/EnhancedAudioRemux.cs", "sourcePath", 1, ENH),
        ("Services/Video/Enhance/EnhancedAudioRemux.cs", "picturePath", 1, ENH),
        ("Services/Video/Enhance/LocalVideoEnhancer.cs", "sourcePath", 1, ENH),
        // FOLLOW-UP: outside ENG-3's three steps, no format whitelist today; to be reviewed in their own ticket.
        ("Execution/StepExecutors/VoiceoverStepExecutor.cs", "inPath", 1, "FOLLOW-UP: voiceover line tempo stretch, no whitelist"),
        ("Agents/Tools/ReactRemotionSandboxTools.cs", "sourcePath", 1, "FOLLOW-UP: Remotion staging, protocol whitelist only"),
        ("Services/Video/GeneratedClipNormalizer.cs", "inputPath", 1, "FOLLOW-UP: vendor clip, -f mov forced, protocol whitelist only"),
        ("Services/Video/LoudnessNormalizer.cs", "inputPath", 3, "FOLLOW-UP: program loudness passes, no whitelist"),
        ("Services/Video/SpeedRampApplier.cs", "inputPath", 1, "FOLLOW-UP: speed ramp re-encode, no whitelist"),
    ];

    private const string DERIV = "the derivative family's own guard: MediaDerivativeInputFormats.Whitelist (security MI-2)";
    private const string ENH = "the enhance family's own guard: EnhanceInputGuards.InputOptions()";

    private static readonly Regex Guard = new(
        @"UserMediaInputFormats\.InputOptions\(\)|\bProtocolWhitelist\b|UserMediaInputFormats\.Whitelist", RegexOptions.Compiled);

    private static readonly Regex EngineForcedFormat = new(@"""(lavfi|concat)""", RegexOptions.Compiled);

    private static readonly Regex InputExpression = new(@"^[\s,;)]*(?:args\.Add\()?\s*([^\s,;)\]]+)", RegexOptions.Compiled);

    /// <summary>How many lines before an <c>-i</c> its options may sit (the longest today is a seek switch).</summary>
    private const int Window = 14;

    internal sealed record InputSite(string File, int Line, string Input, bool Guarded, bool EngineFormat, string Context);

    internal static List<InputSite> ScanInputSites(string sourceDir)
    {
        // Every engine source that builds an "-i" argv literal: a new builder is scanned the day it is added.
        List<string> files = [];
        foreach (string path in Directory.EnumerateFiles(sourceDir, "*.cs", SearchOption.AllDirectories))
        {
            if (File.ReadAllText(path).Contains("\"-i\"", StringComparison.Ordinal))
                files.Add(Path.GetRelativePath(sourceDir, path).Replace('\\', '/'));
        }

        List<InputSite> sites = [];
        foreach (string rel in files.OrderBy(f => f, StringComparer.Ordinal))
        {
            string full = Path.Combine(sourceDir, rel);
            // Comment lines are blanked (kept as empty lines so line numbers stay right).
            string[] lines = File.ReadAllLines(full)
                .Select(l => l.TrimStart().StartsWith("//", StringComparison.Ordinal) ? string.Empty : l)
                .ToArray();
            string code = string.Join('\n', lines);
            int[] lineStarts = [0, .. code.Select((c, i) => (c, i)).Where(t => t.c == '\n').Select(t => t.i + 1)];

            int previousEnd = 0;
            int at = 0;
            while ((at = code.IndexOf("\"-i\"", at, StringComparison.Ordinal)) >= 0)
            {
                int line = Array.FindLastIndex(lineStarts, s => s <= at);
                int windowStart = Math.Max(previousEnd, lineStarts[Math.Max(0, line - Window)]);
                string before = code[windowStart..at];
                System.Text.RegularExpressions.Match input = InputExpression.Match(code[(at + 4)..]);
                string expression = input.Success ? input.Groups[1].Value : "<unknown>";
                sites.Add(new InputSite(rel, line + 1, expression, Guard.IsMatch(before), EngineForcedFormat.IsMatch(before), before));
                previousEnd = at + 4;
                at += 4;
            }
        }

        return sites;
    }

    [Fact]
    public void Every_user_media_input_in_the_engines_argv_builders_carries_both_whitelists()
    {
        List<InputSite> sites = ScanInputSites(EngineSourceDir());
        sites.Count.Should().BeGreaterThan(40, "the scan must find the engine's input sites, or it proves nothing");

        (List<string> unguarded, Dictionary<(string, string), int> exemptSeen) = Classify(sites);

        unguarded.Should().BeEmpty(
            "every user-media -i needs UserMediaInputFormats.InputOptions() before it (ffmpeg applies them to the next input " +
            "only); an engine-authored input belongs in the commented exemption list of this test");
        foreach ((string file, string input, int count, string why) in Exempt)
            exemptSeen.GetValueOrDefault((file, input)).Should().Be(count, $"{file} -i {input} is exempt for {count} site(s): {why}");
    }

    internal static (List<string> Unguarded, Dictionary<(string, string), int> ExemptSeen) Classify(IEnumerable<InputSite> sites)
    {
        List<string> unguarded = [];
        Dictionary<(string, string), int> exemptSeen = [];
        foreach (InputSite site in sites)
        {
            if (site.Guarded || site.EngineFormat)
                continue;

            (string File, string Input, int Sites, string Why) exempt = Exempt.FirstOrDefault(e => e.File == site.File && e.Input == site.Input);
            if (exempt.File is null)
            {
                unguarded.Add($"{site.File}:{site.Line} -i {site.Input}");
                continue;
            }

            exemptSeen[(site.File, site.Input)] = exemptSeen.GetValueOrDefault((site.File, site.Input)) + 1;
        }

        return (unguarded, exemptSeen);
    }

    [Fact]
    public void The_alias_fields_and_the_derivative_exemption_are_what_the_drift_guard_assumes()
    {
        string dir = EngineSourceDir();
        foreach (string rel in new[] { "Services/Video/FfmpegArgvBuilder.cs", "Services/Video/SceneCutFinder.cs" })
            File.ReadAllText(Path.Combine(dir, rel)).Should().Contain("ProtocolWhitelist = UserMediaInputFormats.InputOptions();", rel);

        InputSite derivative = ScanInputSites(dir).Single(s => s.File == "Services/Video/FfmpegArgvBuilder.cs" && !s.Guarded);
        derivative.Context.Should().Contain("MediaDerivativeInputFormats.Whitelist");
    }

    [Fact]
    public void The_drift_guard_catches_an_unguarded_site_and_accepts_a_guarded_one()
    {
        string dir = Path.Combine(_dir, "src");
        Directory.CreateDirectory(Path.Combine(dir, "Services", "Video"));
        File.WriteAllText(Path.Combine(dir, "Services", "Video", "NewBuilder.cs"),
            "class A {\n" +
            "  string[] Guarded(string p) => [.. UserMediaInputFormats.InputOptions(), \"-i\", p];\n" +
            "  string[] Lavfi() => [\"-f\", \"lavfi\", \"-i\", \"sine\"];\n" +
            "  string[] Bare(string upload) => [\"-y\", \"-i\", upload];\n" +
            "}\n");
        List<InputSite> sites = ScanInputSites(dir);

        sites.Should().HaveCount(3);
        sites.Single(s => s.Input == "p").Guarded.Should().BeTrue();
        sites.Single(s => s.Input == "\"sine\"").EngineFormat.Should().BeTrue();
        Classify(sites).Unguarded.Should().Equal(["Services/Video/NewBuilder.cs:4 -i upload"],
            "a new builder file with a bare -i is scanned without being named anywhere, and reported");
    }

    // ---------------------------------------------------------------------------------------------
    // Staging: the staged copy's extension
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Execution/StepExecutors/VideoAnalyzeStepExecutor.cs", 2)]
    [InlineData("Execution/StepExecutors/VideoCompileStepExecutor.cs", 6)]
    [InlineData("Execution/StepExecutors/TimelineRenderStepExecutor.cs", 1)]
    public void Every_staging_site_names_the_copy_with_the_safe_extension(string rel, int sites)
    {
        string text = File.ReadAllText(Path.Combine(EngineSourceDir(), rel));

        Regex.Matches(text, @"UserMediaInputFormats\.SafeExtension\(").Count.Should().Be(
            sites, "each staging site of a customer's media names the copy through SafeExtension (a playlist extension is never kept)");
        Regex.IsMatch(text, @"Path\.GetExtension\([^)]*\)\s*switch\s*\{\s*""""").Should().BeFalse(
            "the raw-extension staging shape is what ENG-3 replaced");
    }

    // ---------------------------------------------------------------------------------------------
    // The whitelist and the refused set, pinned
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void The_whitelist_is_pinned_as_the_ceo_accepted_it()
    {
        UserMediaInputFormats.Whitelist.Should().Be(
            "mov,matroska,mp3,wav,aac,flac,ogg,avi,mpegts,mpeg,flv,asf,aiff,caf,w64,mxf," +
            "ac3,image2,jpeg_pipe,png_pipe,webp_pipe,bmp_pipe,tiff_pipe,jpegls_pipe,gif,apng",
            "a whitelist edit is a product decision (formats a customer can upload), never a drive-by change");
    }

    /// <summary>The formats both reviews listed as refused (or dropped) since ENG-3: stated and pinned here.</summary>
    [Theory]
    [InlineData("svg_pipe")]
    [InlineData("j2k_pipe")]
    [InlineData("jpegxl_pipe")]
    [InlineData("exr_pipe")]
    [InlineData("dpx_pipe")]
    [InlineData("ico")]
    [InlineData("ppm_pipe")]
    [InlineData("pgm_pipe")]
    [InlineData("pbm_pipe")]
    [InlineData("pam_pipe")]
    [InlineData("qoi_pipe")]
    [InlineData("h264")]
    [InlineData("hevc")]
    [InlineData("ivf")]
    [InlineData("nut")]
    [InlineData("rm")]
    [InlineData("yuv4mpegpipe")]
    [InlineData("au")]
    [InlineData("eac3")]
    [InlineData("tta")]
    [InlineData("wv")]
    [InlineData("mpegvideo")]
    [InlineData("dv")]
    [InlineData("voc")]
    [InlineData("hls")]
    [InlineData("concat")]
    [InlineData("dash")]
    [InlineData("sdp")]
    public void A_format_outside_the_whitelist_stays_refused(string demuxer) =>
        UserMediaInputFormats.Whitelist.Split(',').Should().NotContain(demuxer);

    // ---------------------------------------------------------------------------------------------
    // Inputs outside the three steps, and the insert layer
    // ---------------------------------------------------------------------------------------------

    public static IEnumerable<object[]> MoreUserMediaArgv()
    {
        const string In = "/scratch/film.mp4";
        yield return ["review audio facts", ReviewAudioFacts.MeasureArgs(In).ToArray()];
        yield return ["review film look", FfmpegReviewFilmFacts.LookArgs(In, "/scratch/l.jpg", 1.5, null).ToArray()];
        yield return ["segmentation vendor copy", SegmentationMaskArgvBuilder.BuildVendorCopyArgs(In, "/scratch/v.mp4", 2000).ToArray()];
        yield return ["derivative still", StillsArgvBuilder.Build(In, "/scratch/s.jpg", 1500)];
    }

    [Theory]
    [MemberData(nameof(MoreUserMediaArgv))]
    public void Every_user_media_input_outside_the_three_steps_carries_both_whitelists(string name, string[] argv)
    {
        name.Should().NotBeEmpty();
        UserMediaInputHardeningTests.AssertEveryFileInputGuarded(argv);
    }

    [Fact]
    public void A_timeline_insert_layer_reads_its_asset_through_both_whitelists()
    {
        InsertClip insert = new(Guid.NewGuid(), "Insert", 0, 30);
        LocalAsset asset = new("/scratch/asset-001.mp4", "fp", new MediaProbeResult(1, 10, 1, 108, 192, "h264", null, 0));

        UserMediaInputHardeningTests.AssertEveryFileInputGuarded(
            InsertLayerBuilder.InputArgs(insert, asset, new RenderRegion(10, 40, []), 10).ToArray());
    }

    // ---------------------------------------------------------------------------------------------
    // The probe classes a non-media file as the customer's input
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("source.bin: Invalid data found when processing input", true)]
    [InlineData("[hls @ 0x1] Format not on whitelist 'mov,...'", true)]
    [InlineData("Conversion failed: Cannot allocate memory", false)]
    [InlineData("", false)]
    public async Task The_probe_answers_a_file_no_whitelisted_demuxer_reads_as_a_refusal(string stdErr, bool refused)
    {
        Mock<IVideoToolRunner> runner = new();
        runner.Setup(r => r.RunFfprobeAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VideoToolResult(1, string.Empty, stdErr, false));
        FfprobeMediaProbe probe = new(runner.Object, Options.Create(new VideoEditingOptions()), NullLogger<FfprobeMediaProbe>.Instance);

        Exception? thrown = await Record.ExceptionAsync(() => probe.ProbeAsync(Path.Combine(_dir, "source.bin"), CancellationToken.None));

        thrown.Should().BeAssignableTo<InvalidOperationException>("an unreadable file stays an InvalidOperationException for callers that drop it");
        (thrown is MediaFormatNotAllowedException).Should().Be(refused);
    }

    private FfmpegVideoToolRunner Runner() =>
        new(Options.Create(new VideoEditingOptions()), NullLogger<FfmpegVideoToolRunner>.Instance);

    [FfmpegToolsFact]
    public async Task A_tga_or_pam_picture_and_a_playlist_under_a_media_name_are_refused_as_the_customers_input()
    {
        FfmpegVideoToolRunner runner = Runner();
        FfprobeMediaProbe probe = new(runner, Options.Create(new VideoEditingOptions()), NullLogger<FfprobeMediaProbe>.Instance);
        List<string> refused = [];
        foreach (string format in new[] { "tga", "pam" })
        {
            string made = Path.Combine(_dir, "p." + format);
            VideoToolResult encode = await runner.RunFfmpegAsync(
                ["-nostdin", "-hide_banner", "-y", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=s=32x32:d=1", "-frames:v", "1", made],
                TimeSpan.FromSeconds(60), CancellationToken.None);
            encode.Succeeded.Should().BeTrue(encode.StdErr);
            // Staged under the neutral extension, as SafeExtension names an upload it does not know.
            string staged = Path.Combine(_dir, format + "-source" + UserMediaInputFormats.SafeExtension("u/p." + format, ".bin"));
            File.Copy(made, staged);
            refused.Add(staged);
        }

        string playlist = Path.Combine(_dir, "upload.mp4");
        await File.WriteAllTextAsync(playlist, "#EXTM3U\n#EXT-X-TARGETDURATION:1\n#EXTINF:1.0,\n" + refused[0] + "\n#EXT-X-ENDLIST\n");
        refused.Add(playlist);

        foreach (string path in refused)
        {
            Func<Task> act = () => probe.ProbeAsync(path, CancellationToken.None);
            await act.Should().ThrowAsync<MediaFormatNotAllowedException>(path);
        }
    }

    [FfmpegToolsFact]
    public async Task The_review_facts_refuse_a_dash_manifest_named_film_mp4()
    {
        FfmpegVideoToolRunner runner = Runner();
        string mpd = Path.Combine(_dir, "m.mpd");
        VideoToolResult made = await runner.RunFfmpegAsync(
            ["-nostdin", "-hide_banner", "-y", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=s=64x64:r=10:d=2",
             "-f", "lavfi", "-i", "sine=d=2", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-f", "dash", mpd],
            TimeSpan.FromSeconds(60), CancellationToken.None);
        made.Succeeded.Should().BeTrue(made.StdErr);
        string film = Path.Combine(_dir, "film.mp4");
        File.Copy(mpd, film);

        // The pre-fix shape: with the protocol whitelist alone the manifest is read and its segments decoded.
        string before = Path.Combine(_dir, "before.jpg");
        VideoToolResult open = await runner.RunFfmpegAsync(
            ["-nostdin", "-hide_banner", "-y", "-loglevel", "error", "-protocol_whitelist", "file", "-i", film, "-frames:v", "1", before],
            TimeSpan.FromSeconds(60), CancellationToken.None);
        open.Succeeded.Should().BeTrue("the fixture must be a working manifest, or the refusals below prove nothing: " + open.StdErr);

        string look = Path.Combine(_dir, "look.jpg");
        VideoToolResult lookRun = await runner.RunFfmpegAsync(
            ["-loglevel", "error", .. FfmpegReviewFilmFacts.LookArgs(film, look, 0.5, null)], TimeSpan.FromSeconds(60), CancellationToken.None);
        lookRun.Succeeded.Should().BeFalse();
        UserMediaInputFormats.IsWhitelistRefusal(lookRun.StdErr).Should().BeTrue(lookRun.StdErr);
        File.Exists(look).Should().BeFalse();

        VideoToolResult audio = await runner.RunFfmpegAsync(
            ["-loglevel", "error", .. ReviewAudioFacts.MeasureArgs(film)], TimeSpan.FromSeconds(60), CancellationToken.None);
        audio.Succeeded.Should().BeFalse();
        UserMediaInputFormats.IsWhitelistRefusal(audio.StdErr).Should().BeTrue(audio.StdErr);
    }

    [FfmpegToolsFact]
    public async Task A_truncated_or_empty_mp4_is_refused_with_the_damaged_file_message_not_the_playlist_one()
    {
        FfmpegVideoToolRunner runner = Runner();
        FfprobeMediaProbe probe = new(runner, Options.Create(new VideoEditingOptions()), NullLogger<FfprobeMediaProbe>.Instance);
        string whole = Path.Combine(_dir, "whole.mp4");
        VideoToolResult made = await runner.RunFfmpegAsync(
            ["-nostdin", "-hide_banner", "-y", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=s=64x64:r=10:d=2",
             "-c:v", "libx264", "-pix_fmt", "yuv420p", whole],
            TimeSpan.FromSeconds(60), CancellationToken.None);
        made.Succeeded.Should().BeTrue(made.StdErr);
        byte[] bytes = await File.ReadAllBytesAsync(whole);
        string truncated = Path.Combine(_dir, "truncated.mp4");
        await File.WriteAllBytesAsync(truncated, bytes[..(bytes.Length / 3)]);
        string empty = Path.Combine(_dir, "empty.mp4");
        await File.WriteAllBytesAsync(empty, []);

        foreach (string path in new[] { truncated, empty })
        {
            Func<Task> act = () => probe.ProbeAsync(path, CancellationToken.None);
            MediaFormatNotAllowedException refusal = (await act.Should().ThrowAsync<MediaFormatNotAllowedException>(path)).Which;
            refusal.Unrecognised.Should().BeTrue(path);
            string message = UserMediaInputFormats.MessageFor(refusal);
            message.Should().Contain("damaged or incomplete").And.NotContain("playlist");
        }

        FailureClassifier.ForCode(UserMediaInputFormats.RefusedCode).Should().Be(FailureClass.UserInput, "same code and class as the playlist refusal");
    }

    [Fact]
    public void A_whitelist_refusal_keeps_the_playlist_message()
    {
        UserMediaInputFormats.MessageFor(new MediaFormatNotAllowedException("a.mp4")).Should().Be(UserMediaInputFormats.RefusedMessage);
        UserMediaInputFormats.MessageFor(new MediaFormatNotAllowedException("a.mp4", unrecognised: true)).Should().Be(UserMediaInputFormats.UnreadableMessage);
    }
}
