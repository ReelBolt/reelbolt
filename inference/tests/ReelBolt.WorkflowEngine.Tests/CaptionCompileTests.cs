using System;
using System.Collections.Generic;
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
using ReelBolt.Shared.Moderation;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Storage;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Burned captions, dialogue gating and the intelligibility facts through the REAL
/// <see cref="VideoCompileStepExecutor"/> (docs/voiceover.md "Captions" / "Pickups and dubs" /
/// "Review facts"). The first test is the load-bearing one: with <c>EnableVoiceover=true</c> and
/// <c>EnableCaptions=false</c> (the default) the filtergraph and the output-summary key set are
/// exactly the phase-1 voiceover shape — no drawtext, no <c>[vcap]</c>, no <c>captions</c> key.
/// Fixture builders are copies of <see cref="VoiceoverBackwardCompatibilityTests"/>' so this file
/// observes the executor the same way.
/// </summary>
[Collection(VideoFilterProbeCacheCollection.Name)]
public class CaptionCompileTests
{
    private static readonly Guid ProjectId = Guid.NewGuid();
    private const string AnalysisKey = "projects/p/agentFiles/video-analysis/e/step-1-analysis.json";
    private const string SourceVideoKey = "projects/p/outputFiles/e/render.mp4";

    private static readonly (string Text, double S, double E)[] Words =
    [
        ("deploys", 0.0, 0.4), ("go", 0.4, 0.6), ("from", 0.6, 0.8), ("forty", 0.8, 1.2),
        ("minutes", 1.2, 1.7), ("to", 1.7, 1.9), ("four", 1.9, 2.4)
    ];

    [Fact]
    public async Task EnableCaptions_false_leaves_the_voiceover_filtergraph_and_summary_shape_untouched()
    {
        StepExecutionContext context = CreateContext(
            cfg => cfg with { EnableVoiceover = true, VoiceoverStepOrder = 3 },
            out Mock<IProjectFileWorkspace> workspace, withWords: true);

        IReadOnlyList<string>? args = null;
        StepExecutionResult result = await CreateExecutor(workspace, a => args = a).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        using JsonDocument doc = JsonDocument.Parse(result.Output);
        doc.RootElement.TryGetProperty("captions", out _).Should().BeFalse("the key's ABSENCE is the contract");
        doc.RootElement.TryGetProperty("pickups", out _).Should().BeFalse();
        doc.RootElement.TryGetProperty("voiceover", out JsonElement voiceover).Should().BeTrue();
        voiceover.TryGetProperty("dialogue", out _).Should().BeFalse("OverMusic gates nothing");

        string graph = ExtractFilterGraph(args!);
        graph.Should().NotContain("drawtext").And.NotContain("[vcap]").And.NotContain("volume=eval=frame");
        graph.Should().Contain("[abase]", "the voiceover mix itself is still there");
        graph.Should().Contain("[vout]");
    }

    // Since captions moved to libass, this is the DEGRADE path: the mocked ffmpeg lists drawtext
    // but no ass filter, so the old drawtext renderer runs and says why. Kept exact so the degrade
    // stays byte-identical to the renderer it falls back to.
    [Fact]
    public async Task Subtitle_captions_are_burned_from_the_spoken_text_through_textfile_paths_and_timed_from_the_words()
    {
        StepExecutionContext context = CreateContext(
            cfg => cfg with { EnableVoiceover = true, VoiceoverStepOrder = 3, EnableCaptions = true, CaptionMaxCharsPerCue = 22 },
            out Mock<IProjectFileWorkspace> workspace, withWords: true);

        IReadOnlyList<string>? args = null;
        Dictionary<string, string> cueFiles = new();
        StepExecutionResult result = await CreateExecutor(workspace, a =>
        {
            args = a;
            // Read the cue files WHILE ffmpeg is "running" — they live in the per-step scratch space.
            string graph = ExtractFilterGraph(a);
            foreach (string path in System.Text.RegularExpressions.Regex.Matches(graph, @"textfile='([^']+)'").Select(m => m.Groups[1].Value))
                cueFiles[Path.GetFileName(path)] = File.ReadAllText(path);
        }).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        using JsonDocument doc = JsonDocument.Parse(result.Output);
        JsonElement captions = doc.RootElement.GetProperty("captions");
        captions.GetProperty("applied").GetBoolean().Should().BeTrue();
        captions.GetProperty("style").GetString().Should().Be("Subtitle");
        captions.GetProperty("cueCount").GetInt32().Should().Be(2);
        captions.GetProperty("renderer").GetString().Should().Be("drawtext");
        captions.GetProperty("degradeReason").GetString().Should().Be("libass_unavailable");

        string filterGraph = ExtractFilterGraph(args!);
        filterGraph.Should().Contain("[vcap]drawbox=");
        filterGraph.Should().NotContain("ass=filename");
        filterGraph.Should().EndWith("[vout]");
        filterGraph.Should().NotContain("deploys").And.NotContain("forty", "text never enters the filter string");
        filterGraph.Should().Contain("expansion=none");
        // The line starts at output 2.0 (its declared source start, kept whole), so the first cue
        // runs from the first word to the last word of its chunk, in OUTPUT seconds.
        filterGraph.Should().Contain("enable='gte(t,2)*lt(t,3.2)'");
        filterGraph.Should().Contain("enable='gte(t,3.2)*lt(t,4.4)'");

        cueFiles.Should().ContainKey("cap-0.txt").WhoseValue.Should().Be("deploys go from forty");
        cueFiles.Should().ContainKey("cap-2.txt").WhoseValue.Should().Be("minutes to four");

        // The intelligibility facts ride on the voiceover node: WER is measurable (words exist and
        // match the text exactly), headroom is not (the fixture's line bytes are not a real WAV).
        JsonElement voiceover = doc.RootElement.GetProperty("voiceover");
        voiceover.GetProperty("wer").GetProperty("applicable").GetBoolean().Should().BeTrue();
        voiceover.GetProperty("wer").GetProperty("maxWer").GetDouble().Should().Be(0);
        voiceover.GetProperty("headroom").GetProperty("applicable").GetBoolean().Should().BeFalse();
        voiceover.GetProperty("headroom").GetProperty("reason").GetString().Should().Be("not_measurable");
    }

    [Fact]
    public async Task With_libass_the_captions_are_one_ass_filter_over_a_generated_script_using_the_bundled_font()
    {
        StepExecutionContext context = CreateContext(
            cfg => cfg with
            {
                EnableVoiceover = true,
                VoiceoverStepOrder = 3,
                EnableCaptions = true,
                CaptionStyle = CaptionStyle.Karaoke,
                CaptionMaxCharsPerCue = 22,
                CaptionPosition = CaptionPosition.Top
            },
            out Mock<IProjectFileWorkspace> workspace, withWords: true);

        string fontsDir = AssSubtitleBuilderTests.RepoFontsDir();
        IReadOnlyList<string>? args = null;
        string? script = null;
        StepExecutionResult result = await CreateExecutor(workspace, a =>
        {
            args = a;
            System.Text.RegularExpressions.Match m = Regex.Match(ExtractFilterGraph(a), @"ass=filename='([^']+)'");
            if (m.Success)
                script = File.ReadAllText(m.Groups[1].Value);
        }, FiltersWithLibass, fontsDir).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        using JsonDocument doc = JsonDocument.Parse(result.Output);
        JsonElement captions = doc.RootElement.GetProperty("captions");
        captions.GetProperty("applied").GetBoolean().Should().BeTrue();
        captions.GetProperty("renderer").GetString().Should().Be("libass");
        captions.TryGetProperty("degradeReason", out _).Should().BeFalse();
        captions.GetProperty("font").GetString().Should().Be("Montserrat", "Karaoke's automatic font, present in the fonts directory");
        captions.TryGetProperty("fontFallback", out _).Should().BeFalse();
        captions.GetProperty("position").GetString().Should().Be("Top");
        captions.GetProperty("fontSize").GetString().Should().Be("auto");

        string graph = ExtractFilterGraph(args!);
        graph.Should().NotContain("drawtext").And.NotContain("drawbox");
        graph.Should().MatchRegex(@"\[vcap\]ass=filename='[^']+/captions\.ass':fontsdir='" + Regex.Escape(fontsDir) + @"'\[vout\]");
        graph.Should().NotContain("deploys", "text never enters the filter string");

        script.Should().NotBeNull();
        script.Should().Contain("PlayResX: 1920\nPlayResY: 1080\n", "laid out for the canvas actually encoded");
        script.Should().Contain("Style: Caption,Montserrat,70,&H0000FFFF,&H00FFFFFF,&H66000000,&H66000000,-1,0,0,0,100,100,0,0,3,15,0,8,154,154,86,1\n");
        // Line vo0 starts at output 2.0 s; the measured word starts give the \k durations.
        script.Should().Contain("Dialogue: 0,0:00:02.00,0:00:03.20,Caption,,0,0,0,,{\\k40}deploys {\\k20}go {\\k20}from {\\k40}forty\n");
        script.Should().Contain("Dialogue: 0,0:00:03.20,0:00:04.40,Caption,,0,0,0,,{\\k50}minutes {\\k20}to {\\k50}four\n");
    }

    [Fact]
    public async Task A_missing_fonts_directory_falls_back_to_DejaVu_and_says_so()
    {
        StepExecutionContext context = CreateContext(
            cfg => cfg with { EnableVoiceover = true, VoiceoverStepOrder = 3, EnableCaptions = true, CaptionStyle = CaptionStyle.Punch },
            out Mock<IProjectFileWorkspace> workspace, withWords: true);

        IReadOnlyList<string>? args = null;
        StepExecutionResult result = await CreateExecutor(workspace, a => args = a, FiltersWithLibass).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        using JsonDocument doc = JsonDocument.Parse(result.Output);
        JsonElement captions = doc.RootElement.GetProperty("captions");
        captions.GetProperty("font").GetString().Should().Be("DejaVu Sans");
        captions.GetProperty("position").GetString().Should().Be("Center", "Punch's automatic position, resolved");
        captions.GetProperty("fontFallback").GetString().Should().Be("font_not_installed");
        ExtractFilterGraph(args!).Should().Contain("ass=filename=").And.NotContain("fontsdir=", "a directory that does not exist is not passed to libass");
    }

    [Fact]
    public async Task A_caption_colour_outside_the_allowlist_never_reaches_the_drawtext_filter_string()
    {
        // Regression: caption colours were interpolated into drawtext/drawbox unvalidated.
        StepExecutionContext context = CreateContext(
            cfg => cfg with
            {
                EnableVoiceover = true,
                VoiceoverStepOrder = 3,
                EnableCaptions = true,
                CaptionFontColor = "white:fontfile=/etc/passwd",
                CaptionBoxColor = "black@0.6,drawtext=text=x"
            },
            out Mock<IProjectFileWorkspace> workspace, withWords: true);

        IReadOnlyList<string>? args = null;
        StepExecutionResult result = await CreateExecutor(workspace, a => args = a).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        using JsonDocument doc = JsonDocument.Parse(result.Output);
        doc.RootElement.GetProperty("captions").GetProperty("colorFallbacks").EnumerateArray().Select(e => e.GetString())
            .Should().Equal("captionFontColor", "captionBoxColor");

        string graph = ExtractFilterGraph(args!);
        graph.Should().NotContain("/etc/passwd").And.NotContain("text=x");
        graph.Should().Contain("fontcolor=white:").And.Contain("color=black@0.6:");
    }

    [Fact]
    public async Task Karaoke_without_word_alignment_degrades_to_no_captions_with_a_reason_and_an_unchanged_graph()
    {
        StepExecutionContext context = CreateContext(
            cfg => cfg with { EnableVoiceover = true, VoiceoverStepOrder = 3, EnableCaptions = true, CaptionStyle = CaptionStyle.Karaoke },
            out Mock<IProjectFileWorkspace> workspace, withWords: false);

        IReadOnlyList<string>? args = null;
        StepExecutionResult result = await CreateExecutor(workspace, a => args = a).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        using JsonDocument doc = JsonDocument.Parse(result.Output);
        JsonElement captions = doc.RootElement.GetProperty("captions");
        captions.GetProperty("applied").GetBoolean().Should().BeFalse();
        captions.GetProperty("reason").GetString().Should().Be("no_word_alignment");
        captions.GetProperty("dropped").EnumerateArray().Should().ContainSingle()
            .Which.GetProperty("reason").GetString().Should().Be("no_word_alignment");

        ExtractFilterGraph(args!).Should().NotContain("drawtext").And.NotContain("[vcap]");

        doc.RootElement.GetProperty("voiceover").GetProperty("wer").GetProperty("applicable").GetBoolean().Should().BeFalse();
        doc.RootElement.GetProperty("voiceover").GetProperty("wer").GetProperty("reason").GetString().Should().Be("no_word_alignment");
    }

    [Fact]
    public async Task ReplaceDialogue_gates_the_dialogue_cut_before_the_mix_and_reports_the_windows()
    {
        StepExecutionContext context = CreateContext(
            cfg => cfg with { EnableVoiceover = true, VoiceoverStepOrder = 3, VoiceoverMode = VoiceoverMode.ReplaceDialogue, VoiceoverDuckRampMs = 0 },
            out Mock<IProjectFileWorkspace> workspace, withWords: false);

        IReadOnlyList<string>? args = null;
        StepExecutionResult result = await CreateExecutor(workspace, a => args = a).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        string graph = ExtractFilterGraph(args!);
        // The gate sits on the [0:a] dialogue cut, before [abase] hands off to the voiceover mix.
        int gateIndex = graph.IndexOf("volume=eval=frame:volume='(1-(1-0)*gte(t,2)*lt(t,5))'", StringComparison.Ordinal);
        gateIndex.Should().BeGreaterThan(-1);
        gateIndex.Should().BeLessThan(graph.IndexOf("[abase]", StringComparison.Ordinal));

        using JsonDocument doc = JsonDocument.Parse(result.Output);
        JsonElement dialogue = doc.RootElement.GetProperty("voiceover").GetProperty("dialogue");
        dialogue.GetProperty("mode").GetString().Should().Be("ReplaceDialogue");
        dialogue.GetProperty("muteWindows").EnumerateArray().Should().ContainSingle();
    }

    // -----------------------------------------------------------------------
    // T-006: captions are moderated once, before any scratch text file or argv.
    // -----------------------------------------------------------------------

    // =======================================================================
    // CaptionSource.Transcript: subtitles from the clip's own speech (docs/voiceover.md "Subtitles from the transcript").
    // =======================================================================

    private static readonly VideoAnalysisSegment[] TranscriptSegments =
    [
        new("t0", "s0", 1.0, 3.0, "Welcome to the product tour."),
        new("t1", "s0", 4.0, 6.0, "Today we look at deploys.")
    ];

    private static async Task<(StepExecutionResult Result, IReadOnlyList<string>? Args, Dictionary<string, string> Uploads)> RunTranscriptAsync(
        Func<VideoCompileStepConfig, VideoCompileStepConfig> configure,
        IReadOnlyList<VideoAnalysisSegment>? segments,
        string? translationOutput = null,
        string filters = FiltersWithLibass,
        string? staleTranslationOutput = null)
    {
        StepExecutionContext context = CreateContext(
            cfg => configure(cfg with { EnableCaptions = true, CaptionSource = CaptionSource.Transcript }),
            out Mock<IProjectFileWorkspace> workspace, withWords: false, segments: segments, translationOutput: translationOutput,
            staleTranslationOutput: staleTranslationOutput);

        IReadOnlyList<string>? args = null;
        VideoCompileStepExecutor executor = CreateExecutor(workspace, a => args = a, filters);

        Dictionary<string, string> uploads = new(StringComparer.Ordinal);
        workspace
            .Setup(w => w.UploadArtifactAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<string>()))
            .ReturnsAsync((Guid _, string local, string name, string mime, CancellationToken _, string _) =>
            {
                if (name.EndsWith(".srt", StringComparison.Ordinal) || name.EndsWith(".vtt", StringComparison.Ordinal))
                    uploads[name + "|" + mime] = File.ReadAllText(local);
                return $"projects/p/agentFiles/{name}";
            });
        workspace.SetupRegisteredRenderPublish();

        StepExecutionResult result = await executor.ExecuteAsync(context);
        return (result, args, uploads);
    }

    [Fact]
    public async Task Transcript_captions_need_no_voiceover_and_write_SRT_and_VTT_from_the_same_cues()
    {
        var (result, args, uploads) = await RunTranscriptAsync(cfg => cfg with { CaptionStyle = CaptionStyle.Subtitle }, TranscriptSegments);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        ExtractFilterGraph(args!).Should().Contain("ass=filename=", "the transcript cues are burned although EnableVoiceover is off");

        using JsonDocument doc = JsonDocument.Parse(result.Output);
        JsonElement captions = doc.RootElement.GetProperty("captions");
        captions.GetProperty("applied").GetBoolean().Should().BeTrue();
        captions.GetProperty("source").GetString().Should().Be("Transcript");
        captions.GetProperty("cueCount").GetInt32().Should().Be(2);
        JsonElement keys = doc.RootElement.GetProperty("subtitleStorageKeys");
        keys.GetProperty("srt").GetString().Should().EndWith("step-4-subtitles.srt");
        keys.GetProperty("vtt").GetString().Should().EndWith("step-4-subtitles.vtt");

        // The clip is kept whole (source 1:1 to output), so source seconds are output seconds.
        string srt = uploads.Single(u => u.Key.EndsWith("|application/x-subrip", StringComparison.Ordinal)).Value;
        string vtt = uploads.Single(u => u.Key.EndsWith("|text/vtt", StringComparison.Ordinal)).Value;
        srt.Should().Be(
            "1\r\n00:00:01,000 --> 00:00:03,000\r\nWelcome to the product tour.\r\n\r\n" +
            "2\r\n00:00:04,000 --> 00:00:06,000\r\nToday we look at deploys.\r\n\r\n");
        vtt.Should().Be(
            "WEBVTT\n\n00:00:01.000 --> 00:00:03.000\nWelcome to the product tour.\n\n" +
            "00:00:04.000 --> 00:00:06.000\nToday we look at deploys.\n\n");
    }

    [Fact]
    public async Task Transcript_captions_with_drawtext_only_still_burn_and_write_the_files_without_the_caption_text_in_argv()
    {
        var (result, args, uploads) = await RunTranscriptAsync(cfg => cfg, TranscriptSegments, filters: FiltersWithoutLibass);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        string graph = ExtractFilterGraph(args!);
        graph.Should().Contain("drawtext").And.Contain("textfile=").And.Contain("expansion=none");
        graph.Should().NotContain("Welcome", "caption text goes through textfile=, never into the filter string");
        args!.Should().NotContain(a => a.Contains("Welcome", StringComparison.Ordinal));
        uploads.Should().HaveCount(2);
    }

    [Fact]
    public async Task Transcript_captions_without_a_transcript_fail_with_a_named_code()
    {
        var (result, args, uploads) = await RunTranscriptAsync(cfg => cfg, segments: null);

        result.Status.Should().Be(StepStatus.Failed);
        result.Output.Should().Contain("CAPTIONS_NO_TRANSCRIPT");
        args.Should().BeNull("no encode was started");
        uploads.Should().BeEmpty();
    }

    [Fact]
    public async Task A_translation_replaces_the_words_and_keeps_the_segment_timing()
    {
        string translation = JsonSerializer.Serialize(new
        {
            lines = new object[]
            {
                new { anchorId = "t0", text = "Benvenuti al tour del prodotto.", delivery = "", reason = "" },
                new { anchorId = "t1", text = "Oggi vediamo i deploy.", delivery = "", reason = "" },
                new { anchorId = "t9", text = "A segment that does not exist.", delivery = "", reason = "" }
            },
            planRationale = "Italian"
        });

        var (result, _, uploads) = await RunTranscriptAsync(cfg => cfg with { CaptionTranslationStepOrder = 5 }, TranscriptSegments, translation);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        string srt = uploads.Single(u => u.Key.EndsWith("|application/x-subrip", StringComparison.Ordinal)).Value;
        srt.Should().Be(
            "1\r\n00:00:01,000 --> 00:00:03,000\r\nBenvenuti al tour del prodotto.\r\n\r\n" +
            "2\r\n00:00:04,000 --> 00:00:06,000\r\nOggi vediamo i deploy.\r\n\r\n");
        using JsonDocument doc = JsonDocument.Parse(result.Output);
        JsonElement info = doc.RootElement.GetProperty("captions").GetProperty("transcript");
        info.GetProperty("translation").GetString().Should().Be("applied");
        info.GetProperty("translatedCount").GetInt32().Should().Be(2);
        info.GetProperty("untranslatedCount").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task The_latest_run_of_the_translation_step_is_the_one_burned()
    {
        string Plan(string a, string b) => JsonSerializer.Serialize(new { lines = new object[] { new { anchorId = "t0", text = a }, new { anchorId = "t1", text = b } }, planRationale = "x" });

        var (result, _, uploads) = await RunTranscriptAsync(cfg => cfg with { CaptionTranslationStepOrder = 5 }, TranscriptSegments,
            Plan("Nuovo uno.", "Nuovo due."), staleTranslationOutput: Plan("Vecchio uno.", "Vecchio due."));

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        string srt = uploads.Single(u => u.Key.EndsWith("|application/x-subrip", StringComparison.Ordinal)).Value;
        srt.Should().Contain("Nuovo uno.").And.NotContain("Vecchio");
    }

    [Fact]
    public async Task A_config_tuned_for_a_narrow_burned_style_still_writes_full_width_subtitle_files()
    {
        VideoAnalysisSegment[] segments = [new("t0", "s0", 1.0, 7.0, "This sentence is deliberately longer than twelve characters in total.")];

        var (result, _, uploads) = await RunTranscriptAsync(cfg => cfg with { CaptionMaxCharsPerCue = 12, CaptionStyle = CaptionStyle.Punch }, segments);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        string srt = uploads.Single(u => u.Key.EndsWith("|application/x-subrip", StringComparison.Ordinal)).Value;
        srt.Should().Contain("This sentence is deliberately longer");
    }

    [Fact]
    public async Task An_unreadable_translation_keeps_the_original_words_and_says_so()
    {
        var (result, _, uploads) = await RunTranscriptAsync(cfg => cfg with { CaptionTranslationStepOrder = 5 }, TranscriptSegments, "not json at all");

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        uploads.Single(u => u.Key.EndsWith("|application/x-subrip", StringComparison.Ordinal)).Value.Should().Contain("Welcome to the product tour.");
        using JsonDocument doc = JsonDocument.Parse(result.Output);
        doc.RootElement.GetProperty("captions").GetProperty("transcript").GetProperty("translation").GetString().Should().Be("translation_output_invalid");
    }

    [Fact]
    public async Task The_default_voiceover_source_has_no_transcript_keys_and_writes_no_subtitle_files()
    {
        StepExecutionContext context = CreateContext(
            cfg => cfg with { EnableVoiceover = true, VoiceoverStepOrder = 3, EnableCaptions = true },
            out Mock<IProjectFileWorkspace> workspace, withWords: true, segments: TranscriptSegments);

        StepExecutionResult result = await CreateExecutor(workspace, filters: FiltersWithLibass).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        using JsonDocument doc = JsonDocument.Parse(result.Output);
        doc.RootElement.TryGetProperty("subtitleStorageKeys", out _).Should().BeFalse();
        JsonElement captions = doc.RootElement.GetProperty("captions");
        captions.TryGetProperty("source", out _).Should().BeFalse();
        captions.TryGetProperty("transcript", out _).Should().BeFalse();
        captions.TryGetProperty("subtitleFiles", out _).Should().BeFalse();
    }

    private sealed class FakeGate : IContentModerationGate
    {
        public FakeGate(ModerationAction action, bool available = true)
        {
            Decision = new ModerationDecision(
                action, available, action == ModerationAction.Review, null, Message, "hash");
        }

        public const string Message = "This caption cannot be rendered.";
        public ModerationDecision Decision { get; }
        public List<ModerationInput> Inputs { get; } = [];
        public List<ModerationAttribution> Attributions { get; } = [];

        public Task<ModerationDecision> ModerateAsync(
            ModerationInput input, ModerationAttribution attribution, CancellationToken ct)
        {
            Inputs.Add(input);
            Attributions.Add(attribution);
            return Task.FromResult(Decision);
        }

        public Task<bool> ScreenGenerationPromptAsync(
            string text, IReadOnlyList<ModerationImageInput> images, ModerationAttribution attribution, CancellationToken ct)
            => Task.FromResult(Decision.Action != ModerationAction.Block);

        public Task<bool> ScreenGeneratedTextAsync(
            string text, string site, ModerationAttribution attribution, CancellationToken ct)
            => Task.FromResult(Decision.Action != ModerationAction.Block);

        public Task<bool> ScreenPublishAsync(
            ModerationInput input, ModerationAttribution attribution, CancellationToken ct)
            => Task.FromResult(Decision.Available && Decision.Action != ModerationAction.Block);
    }

    [Fact]
    public async Task A_blocked_caption_fails_before_any_ffmpeg_call_and_never_writes_a_text_file()
    {
        StepExecutionContext context = CreateContext(
            cfg => cfg with { EnableVoiceover = true, VoiceoverStepOrder = 3, EnableCaptions = true },
            out Mock<IProjectFileWorkspace> workspace, withWords: true);
        bool anyFfmpegCall = false;
        var gate = new FakeGate(ModerationAction.Block);

        StepExecutionResult result = await CreateExecutor(workspace, anyFfmpegCall: () => anyFfmpegCall = true, moderation: gate)
            .ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Failed);
        result.FailureClass.Should().Be(FailureClass.UserInput);
        result.ErrorDetails.Should().Be(FakeGate.Message);
        anyFfmpegCall.Should().BeFalse("moderation runs before the availability probe and the render");
        gate.Inputs.Should().ContainSingle();
        gate.Inputs[0].Text.Should().Contain("deploys go from forty minutes to four");
        gate.Attributions[0].Surface.Should().Be(ModerationSurface.GeneratedText);
        gate.Attributions[0].Kind.Should().Be(ModerationContentKind.Text);
    }

    [Theory]
    [InlineData(ModerationAction.Allow, true)]
    [InlineData(ModerationAction.Review, true)]
    [InlineData(ModerationAction.Unavailable, false)]
    public async Task Captions_proceed_on_allow_review_and_unavailable(ModerationAction action, bool available)
    {
        StepExecutionContext context = CreateContext(
            cfg => cfg with { EnableVoiceover = true, VoiceoverStepOrder = 3, EnableCaptions = true, CaptionMaxCharsPerCue = 22 },
            out Mock<IProjectFileWorkspace> workspace, withWords: true);
        var gate = new FakeGate(action, available);

        StepExecutionResult result = await CreateExecutor(workspace, moderation: gate).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        using JsonDocument doc = JsonDocument.Parse(result.Output);
        doc.RootElement.GetProperty("captions").GetProperty("applied").GetBoolean().Should().BeTrue();
        gate.Inputs.Should().ContainSingle();
    }

    [Fact]
    public async Task EnableCaptions_false_does_not_call_the_gate_and_keeps_the_graph_unchanged()
    {
        StepExecutionContext context = CreateContext(
            cfg => cfg with { EnableVoiceover = true, VoiceoverStepOrder = 3 },
            out Mock<IProjectFileWorkspace> workspace, withWords: true);
        IReadOnlyList<string>? args = null;
        var gate = new FakeGate(ModerationAction.Block);

        StepExecutionResult result = await CreateExecutor(workspace, a => args = a, moderation: gate).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        gate.Inputs.Should().BeEmpty("the captions-off path never reaches the generated-text surface");
        string graph = ExtractFilterGraph(args!);
        graph.Should().NotContain("drawtext").And.NotContain("[vcap]");
        graph.Should().Contain("[abase]").And.Contain("[vout]");
    }

    [Fact]
    public async Task EnableCaptions_with_stream_copy_is_a_hard_config_error()
    {
        StepExecutionContext context = CreateContext(
            cfg => cfg with { Mode = VideoCompileMode.StreamCopy, AllowKeyframeSnapping = true, EnableCaptions = true },
            out Mock<IProjectFileWorkspace> workspace, withWords: false);

        StepExecutionResult result = await CreateExecutor(workspace).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Failed);
        using JsonDocument doc = JsonDocument.Parse(result.Output);
        doc.RootElement.GetProperty("error").GetProperty("code").GetString().Should().Be("CAPTIONS_REQUIRE_REENCODE");
    }

    // =======================================================================
    // Fixture
    // =======================================================================

    /// <summary>What the mocked <c>ffmpeg -filters</c> reports by default: drawtext but NO libass, so the drawtext degrade path runs.</summary>
    private const string FiltersWithoutLibass = "... drawtext ... perspective alphamerge colorbalance colorlevels hue eq";

    /// <summary>The same build WITH libass — the <c>ass</c> filter listed exactly as a real <c>ffmpeg -filters</c> line shows it.</summary>
    private const string FiltersWithLibass = " .. ass               V->V       Render ASS subtitles onto input video using the libass library.\n" + FiltersWithoutLibass;

    private static VideoCompileStepExecutor CreateExecutor(
        Mock<IProjectFileWorkspace> workspace, Action<IReadOnlyList<string>>? ffmpegArgsCaptured = null,
        string filters = FiltersWithoutLibass, string? captionFontsDir = null,
        Action? anyFfmpegCall = null, IContentModerationGate? moderation = null)
    {
        VideoCompileStepExecutor.ResetDrawtextAvailabilityCacheForTests();
        VideoCompileStepExecutor.ResetAmixNormalizeCacheForTests();
        VideoCompileStepExecutor.ResetPerspectiveAvailabilityCacheForTests();
        VideoCompileStepExecutor.ResetColorGradeFiltersCacheForTests();

        var toolRunner = new Mock<IVideoToolRunner>();
        toolRunner
            .Setup(t => t.RunFfmpegAsync(It.Is<IReadOnlyList<string>>(a => a.Contains("-filters")), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Callback(() => anyFfmpegCall?.Invoke())
            .ReturnsAsync(new VideoToolResult(0, filters, string.Empty, false));
        toolRunner
            .Setup(t => t.RunFfmpegAsync(It.Is<IReadOnlyList<string>>(a => !a.Contains("-filters")), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<string>, TimeSpan, CancellationToken>((args, _, _) => { anyFfmpegCall?.Invoke(); ffmpegArgsCaptured?.Invoke(args); })
            .ReturnsAsync(new VideoToolResult(0, string.Empty, string.Empty, false));
        toolRunner
            .Setup(t => t.RunFfmpegAsync(It.Is<IReadOnlyList<string>>(a => !a.Contains("-filters")), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>>()))
            .Callback<IReadOnlyList<string>, TimeSpan, CancellationToken, Action<string>>((args, _, _, _) => { anyFfmpegCall?.Invoke(); ffmpegArgsCaptured?.Invoke(args); })
            .ReturnsAsync(new VideoToolResult(0, string.Empty, string.Empty, false));
        toolRunner
            .Setup(t => t.RunFfmpegAsync(It.Is<IReadOnlyList<string>>(a => a.Contains("filter=amix")), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VideoToolResult(0, "... normalize ...", string.Empty, false));

        var mediaProbe = new Mock<IMediaProbe>();
        mediaProbe
            .Setup(p => p.ProbeAsync(It.IsAny<IMediaWorkspace>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaProbeResult(10, 30, 1, 1920, 1080, "h264", "aac", 48000));

        workspace
            .Setup(w => w.UploadArtifactAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<string>()))
            .ReturnsAsync("projects/p/agentFiles/video-analysis/e/step-4-edl.json");
        workspace.SetupRegisteredRenderPublish();
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
            ScratchPath = Path.Combine(Path.GetTempPath(), "caption-compile-tests", Guid.NewGuid().ToString("N")),
            MaxConcurrentJobs = 1,
            AnalyzeTimeoutSeconds = 30,
            CompileTimeoutSeconds = 30,
            CaptionFontsDir = captionFontsDir ?? Path.Combine(Path.GetTempPath(), "no-caption-fonts-" + Guid.NewGuid().ToString("N"))
        });

        return new VideoCompileStepExecutor(
            toolRunner.Object, mediaProbe.Object, workspace.Object, new Mock<IServiceScopeFactory>().Object,
            options, NullLogger<VideoCompileStepExecutor>.Instance, moderation: moderation);
    }

    private static StepExecutionContext CreateContext(
        Func<VideoCompileStepConfig, VideoCompileStepConfig> configure,
        out Mock<IProjectFileWorkspace> workspace,
        bool withWords,
        IReadOnlyList<VideoAnalysisSegment>? segments = null,
        string? translationOutput = null,
        string? staleTranslationOutput = null)
    {
        workspace = new Mock<IProjectFileWorkspace>();

        VideoAnalysisArtifact artifact = new(
            Version: 1,
            Media: new VideoAnalysisMedia(10.0, 30, 1, 1920, 1080),
            Shots: [new VideoAnalysisShot("s0", 0.0, 10.0, Audio: new VideoAnalysisShotAudio(-30, -12, 0.8, "Normal"))],
            SilenceSpans: [],
            Segments: segments ?? [],
            Words: [],
            OfferedIds: ["s0"],
            Provenance: new VideoAnalysisProvenance(VideoTranscriptionMode.Off, false, false),
            Placements: null,
            OfferedPlacementIds: null);
        string artifactJson = JsonSerializer.Serialize(artifact, JsonOptions());

        string decisionJson = JsonSerializer.Serialize(new
        {
            keep = new[] { new { fromId = "s0", toId = "s0", reason = "keep" } },
            editRationale = "test",
            suggestedTitle = "Test"
        });

        // One `ok` line at declared start 2.0, 3.0 s long, speaking the sanitized text below.
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
                        text = "deploys go from forty minutes to four",
                        words = withWords ? Words.Select(w => new { text = w.Text, startSec = w.S, endSec = w.E }).ToArray() : null
                    }
                }
            },
            meta = new { }
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });

        workspace
            .Setup(w => w.DownloadStorageKeyToFileAsync(It.IsAny<Guid>(), AnalysisKey, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, string, string, CancellationToken>((_, _, destPath, _) => { File.WriteAllText(destPath, artifactJson); return Task.CompletedTask; });
        workspace
            .Setup(w => w.DownloadStorageKeyToFileAsync(It.IsAny<Guid>(), SourceVideoKey, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, string, string, CancellationToken>((_, _, destPath, _) => { File.WriteAllBytes(destPath, [0x00, 0x01, 0x02]); return Task.CompletedTask; });
        workspace
            .Setup(w => w.DownloadStorageKeyToFileAsync(
                It.IsAny<Guid>(), It.Is<string>(s => s.Contains("/agentFiles/voiceover/", StringComparison.Ordinal)), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, string, string, CancellationToken>((_, _, destPath, _) => { File.WriteAllBytes(destPath, [0x00, 0x01, 0x02]); return Task.CompletedTask; });
        workspace
            .Setup(w => w.ListFilesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProjectWorkspaceFile>());

        VideoCompileStepConfig config = configure(new VideoCompileStepConfig(
            Version: 1,
            Decision: new ExtractInputRef(ExtractInputSource.Step, StepOrder: 2),
            AnalysisStepOrder: 1));

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
        if (staleTranslationOutput is not null)
            history.Add(new StepOutputHistoryEntry(5, "Translate", staleTranslationOutput, OutputStorageKey: null, ArtifactStorageKey: null));
        if (translationOutput is not null)
            history.Add(new StepOutputHistoryEntry(5, "Translate", translationOutput, OutputStorageKey: null, ArtifactStorageKey: null));

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

    private static string ExtractFilterGraph(IReadOnlyList<string> args)
    {
        int scriptIndex = args.ToList().IndexOf("-filter_complex_script");
        if (scriptIndex >= 0)
            return File.ReadAllText(args[scriptIndex + 1]);
        int index = args.ToList().IndexOf("-filter_complex");
        index.Should().BeGreaterThan(-1);
        return args[index + 1];
    }

    private static JsonSerializerOptions JsonOptions() => new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };
}
