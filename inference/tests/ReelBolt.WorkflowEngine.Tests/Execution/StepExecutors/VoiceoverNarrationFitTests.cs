using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Shared.Briefs;
using ReelBolt.Shared.Data.OutputSchemas;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Moderation;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Billing;
using ReelBolt.WorkflowEngine.Services.Briefs;
using ReelBolt.WorkflowEngine.Services.Video;
using ReelBolt.WorkflowEngine.Services.Voiceover;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Execution.StepExecutors;

/// <summary>Narration fit ladder: budget, tempo fit, clear failure, Report policy. No providers.</summary>
public sealed class VoiceoverNarrationFitTests
{
    private static readonly Guid ProjectId = Guid.NewGuid();

    private sealed class Rig
    {
        public VoiceoverCrossExecutionArtifactTests.CountingSpeechSynthesisClient Client { get; } = new();
        public VoiceoverCrossExecutionArtifactTests.InMemoryObjectStoreWorkspace Store { get; }
        public List<string> FfmpegArgs { get; } = [];
        public VoiceoverStepExecutor Executor { get; }

        public Rig(double originalSec, double retimedSec, bool withRunner = true, INarrationLineShortener? shortener = null, IContentModerationGate? moderation = null,
            VoiceoverCrossExecutionArtifactTests.InMemoryObjectStoreWorkspace? store = null,
            ReelBolt.WorkflowEngine.Services.Voiceover.IProjectPronunciationResolver? glossary = null,
            IReadOnlyList<double>? durations = null,
            int? maxAdmittedNarrationWords = null)
        {
            Store = store ?? new();
            var probe = new Mock<IMediaProbe>();
            int probeCursor = 0;
            probe.Setup(p => p.ProbeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string path, CancellationToken _) => new MediaProbeResult(
                    // An explicit per-line duration list wins (clamping at the last entry), so a rig can
                    // model the four production lines with different measured speeches.
                    DurationSec: durations is { Count: > 0 }
                        ? durations[Math.Min(probeCursor++, durations.Count - 1)]
                        : Path.GetFileName(path).StartsWith("voiceover_out_", StringComparison.Ordinal) ? retimedSec : originalSec,
                    FpsNum: 30, FpsDen: 1, Width: 0, Height: 0, VideoCodec: null, AudioCodec: "pcm_s16le", AudioSampleRate: 48000));

            var runner = new Mock<IVideoToolRunner>();
            runner.Setup(r => r.RunFfmpegAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                .Returns((IReadOnlyList<string> args, TimeSpan _, CancellationToken _) =>
                {
                    FfmpegArgs.AddRange(args);
                    File.Copy(args[args.ToList().IndexOf("-i") + 1], args[^1], overwrite: true);
                    return Task.FromResult(new VideoToolResult(0, "", "", false));
                });

            var provider = new ResolvedSpeechSynthesisProvider(Guid.NewGuid(), "p", InferenceProviderKind.FishAudio,
                "http://localhost:9000", "m", "k", 30);
            var resolver = new Mock<IInferenceProviderResolver>();
            resolver.Setup(r => r.ResolveSpeechSynthesisAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>())).ReturnsAsync(provider);
            var factory = new Mock<ISpeechSynthesisClientFactory>();
            factory.Setup(f => f.Get(It.IsAny<ResolvedSpeechSynthesisProvider>())).Returns(Client);
            var align = new Mock<IVoiceoverAlignmentService>();
            align.Setup(a => a.AlignAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(VoiceoverAlignmentResult.NotApplied("test"));

            Executor = new VoiceoverStepExecutor(factory.Object, resolver.Object, probe.Object, Store, align.Object,
                NullLogger<VoiceoverStepExecutor>.Instance,
                toolRunner: withRunner ? runner.Object : null, shortener: shortener, moderation: moderation, glossaryResolver: glossary,
                fitOptions: Microsoft.Extensions.Options.Options.Create(
                    new ReelBolt.WorkflowEngine.Services.Voiceover.VoiceoverFitOptions
                    {
                        MaxAdmittedNarrationWords = maxAdmittedNarrationWords
                            ?? ReelBolt.WorkflowEngine.Services.Voiceover.VoiceoverFitOptions.DefaultMaxAdmittedNarrationWords
                    }));
        }
    }

    private static StepExecutionContext Ctx(params (string Text, double Start)[] lines) => Ctx(NarrationFitPolicy.Enforce, lines);

    private static StepExecutionContext Ctx(NarrationFitPolicy policy, params (string Text, double Start)[] lines)
    {
        var config = new VoiceoverStepConfig(
            new VoiceoverSource(VoiceoverSourceKind.Inline, lines.Select(l => new VoiceoverLine(l.Text, l.Start, null)).ToList()),
            ProviderId: Guid.NewGuid(), Model: "m", NarrationFitPolicy: policy);
        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } });
        var step = new WorkflowStep
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = Guid.NewGuid(),
            AgentDefinitionId = Guid.NewGuid(),
            StepOrder = 1,
            StepType = StepType.Voiceover,
            VoiceoverConfigJson = json
        };
        return new StepExecutionContext
        {
            Execution = new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = ProjectId },
            Step = step,
            AllSteps = [step],
            AccumulatedOutput = string.Empty,
            StepOutputHistory = [],
            CurrentStepIndex = 0,
            IterationCount = 0,
            CorrelationId = "t",
            CancellationToken = default
        };
    }

    private static StepExecutionContext ScriptCtx(params (string Text, int Start)[] scenes)
    {
        var output = new ScriptwriterOutput
        {
            Title = "t",
            DurationInSeconds = 10,
            Narrative = "n",
            Scenes = scenes.Select((sc, i) => new ScriptScene
            {
                SceneId = $"s{i}",
                StartTime = sc.Start,
                Duration = 3,
                Voiceover = sc.Text,
                OnScreenText = [],
                VisualDescription = "v"
            }).ToList()
        };
        var config = new VoiceoverStepConfig(
            new VoiceoverSource(VoiceoverSourceKind.ScriptScenes, ScriptwriterStepOrder: 1),
            ProviderId: Guid.NewGuid(), Model: "m");
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
        var step = new WorkflowStep
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = Guid.NewGuid(),
            AgentDefinitionId = Guid.NewGuid(),
            StepOrder = 2,
            StepType = StepType.Voiceover,
            VoiceoverConfigJson = JsonSerializer.Serialize(config, web)
        };
        return new StepExecutionContext
        {
            Execution = new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = ProjectId },
            Step = step,
            AllSteps = [step],
            AccumulatedOutput = string.Empty,
            StepOutputHistory = [new StepOutputHistoryEntry(1, "Scriptwriter", JsonSerializer.Serialize(output, new JsonSerializerOptions(JsonSerializerDefaults.Web)))],
            CurrentStepIndex = 0,
            IterationCount = 0,
            CorrelationId = "t",
            CancellationToken = default
        };
    }

    private const string SixWords = "one two three four five six";

    /// <summary>A deterministic line of exactly <paramref name="count"/> whitespace-separated words.</summary>
    private static string Words(int count) => string.Join(' ', Enumerable.Range(1, count).Select(i => $"word{i}"));

    [Fact]
    public void The_comfort_budget_is_floor_of_window_times_1_8_and_the_hard_ceiling_is_four_per_second()
    {
        NarrationFitSolver.WordBudget(3.5).Should().Be(6);
        NarrationFitSolver.WordBudget(1.0).Should().Be(1);
        NarrationFitSolver.WordBudget(0).Should().Be(0);
        // The hard physical ceiling (4.0 words/s) is distinct from the 1.8 writing budget.
        NarrationFitSolver.HardWordCeiling(4.0).Should().Be(16);
        NarrationFitSolver.HardWordCeiling(3.5).Should().Be(14);
        NarrationFitSolver.HardWordCeiling(0).Should().Be(0);
        NarrationFitSolver.IsUnrescuable(14, 3.5).Should().BeFalse();
        NarrationFitSolver.IsUnrescuable(15, 3.5).Should().BeTrue();
        NarrationFitSolver.IsUnrescuable(16, 4.0).Should().BeFalse();
        NarrationFitSolver.IsUnrescuable(17, 4.0).Should().BeTrue();
        // The spend bound is a SEPARATE predicate: total admitted words vs the per-step ceiling.
        NarrationFitSolver.IsSynthesisAdmissible(5000, 5000).Should().BeTrue();
        NarrationFitSolver.IsSynthesisAdmissible(5001, 5000).Should().BeFalse();
        NarrationFitSolver.IsSynthesisAdmissible(6, 5).Should().BeFalse();
        NarrationFitSolver.PlanTempo(3.5, 3.5).Should().BeNull();
        NarrationFitSolver.PlanTempo(3.8, 3.5)!.Value.Should().BeInRange(1.08, 1.09);
        NarrationFitSolver.PlanTempo(9.0, 3.5).Should().Be(NarrationFitSolver.MaxTempoFit);
    }

    [Fact]
    public void Planned_tempo_never_leaves_overlap_beyond_tolerance_or_exceeds_max()
    {
        var rng = new Random(7);
        for (int n = 0; n < 500; n++)
        {
            double window = 0.5 + rng.NextDouble() * 8;
            double play = window * (0.5 + rng.NextDouble() * 0.9);
            double? tempo = NarrationFitSolver.PlanTempo(play, window);
            double after = tempo is double t ? play / t : play;
            tempo.GetValueOrDefault(1.0).Should().BeLessThanOrEqualTo(NarrationFitSolver.MaxTempoFit);
            NarrationFitLineResult r = NarrationFitSolver.SolveLine(new NarrationFitLine("x", null, 0, window, after, 5));
            if (play / NarrationFitSolver.MaxTempoFit <= window)
                r.OverflowSec.Should().BeLessThanOrEqualTo(NarrationFitSolver.OverflowToleranceSec);
        }
    }

    [Fact]
    public async Task Last_line_window_comes_from_the_brief_length()
    {
        var rig = new Rig(1.0, 1.0);
        using (RunBriefAmbient.Enter(new RunBriefState(new StoredRunBrief(LengthSec: 2))))
        {
            StepExecutionResult result = await rig.Executor.ExecuteAsync(Ctx(("one two three", 0.0)));
            result.Status.Should().Be(StepStatus.Completed, result.ErrorDetails);
            JsonElement fit = JsonDocument.Parse(result.Output).RootElement.GetProperty("view").GetProperty("lines")[0].GetProperty("fit");
            fit.GetProperty("windowSec").GetDouble().Should().Be(2.0);
            fit.GetProperty("wordBudget").GetInt32().Should().Be(3);
        }
    }

    [Fact]
    public async Task Without_a_brief_the_last_line_has_no_window_as_before()
    {
        var rig = new Rig(9.0, 9.0);
        StepExecutionResult result = await rig.Executor.ExecuteAsync(Ctx(("one two three", 0.0)));
        result.Status.Should().Be(StepStatus.Completed, result.ErrorDetails);
        JsonDocument.Parse(result.Output).RootElement.GetProperty("view").GetProperty("lines")[0]
            .GetProperty("fit").GetProperty("windowSource").GetString().Should().Be("none");
    }

    [Fact]
    public async Task A_line_the_tempo_fit_can_rescue_is_retimed_into_a_new_object_and_reports_zero_overflow()
    {
        var rig = new Rig(originalSec: 3.8, retimedSec: 3.4);
        StepExecutionResult result = await rig.Executor.ExecuteAsync(Ctx((SixWords, 0.0), ("seven", 3.5)));

        result.Status.Should().Be(StepStatus.Completed, result.ErrorDetails);
        rig.FfmpegArgs.Should().Contain(a => a.StartsWith("atempo=", StringComparison.Ordinal));
        JsonElement root = JsonDocument.Parse(result.Output).RootElement;
        JsonElement line0 = root.GetProperty("view").GetProperty("lines")[0];
        line0.GetProperty("storageKey").GetString().Should().Contain("-t1p");
        line0.GetProperty("durationSec").GetDouble().Should().Be(3.4);
        line0.GetProperty("fit").GetProperty("tempo").GetDouble().Should().BeGreaterThan(1.0);
        JsonElement fit = root.GetProperty("meta").GetProperty("fit");
        fit.GetProperty("retimedLineCount").GetInt32().Should().Be(1);
        fit.GetProperty("maxTempoApplied").GetDouble().Should().BeLessThanOrEqualTo(1.15);
        fit.GetProperty("overflowTotalSec").GetDouble().Should().Be(0.0);
        rig.Store.UploadedLineKeys.Should().HaveCount(3, "the original is kept and the retimed one is a new object");
    }

    [Fact]
    public async Task A_line_even_the_tempo_fit_cannot_rescue_fails_with_plain_words()
    {
        var rig = new Rig(originalSec: 5.0, retimedSec: 4.6);
        StepExecutionResult result = await rig.Executor.ExecuteAsync(Ctx((SixWords, 0.0), ("seven", 3.5)));

        result.Status.Should().Be(StepStatus.Failed);
        result.FailureCode.Should().Be("NARRATION_DOES_NOT_FIT", result.ErrorDetails);
        result.FailureClass.Should().Be(FailureClass.UserInput);
        result.UserMessage.Should().Contain("The line for scene 1").And.NotContain("NARRATION_DOES_NOT_FIT");
        result.Retryable.Should().BeFalse();
        // Judged on MEASURED speech: the line was synthesized, retimed to 4.6 s, and 4.6 > 3.5 + 0.1.
        rig.Client.CallCount.Should().Be(2, "both lines are synthesized before the measured verdict");
        result.ErrorDetails.Should().Contain("The line for scene 1 is too long for its scene: it has 6 words and takes 4.6 s of speech but the scene lasts 3.5 s. Shorten the text or lengthen the scene.");
        JsonDocument.Parse(result.Output).RootElement.GetProperty("view").GetProperty("lines").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Without_a_tool_runner_an_overrun_goes_straight_to_the_failure()
    {
        var rig = new Rig(originalSec: 3.8, retimedSec: 3.4, withRunner: false);
        StepExecutionResult result = await rig.Executor.ExecuteAsync(Ctx((SixWords, 0.0), ("seven", 3.5)));
        result.FailureCode.Should().Be("NARRATION_DOES_NOT_FIT", result.ErrorDetails);
        rig.FfmpegArgs.Should().BeEmpty();
    }

    [Fact]
    public async Task Pre_synthesis_refusal_spends_nothing_and_speaks_italian_for_an_italian_run()
    {
        var rig = new Rig(1.0, 1.0);
        using (RunBriefAmbient.Enter(new RunBriefState(new StoredRunBrief(Language: "it"))))
        {
            // 15 words in a 3.5 s window is above the hard ceiling floor(3.5 x 4.0) = 14.
            const string longItalianLine = "uno due tre quattro cinque sei sette otto nove dieci undici dodici tredici quattordici quindici";
            StepExecutionResult result = await rig.Executor.ExecuteAsync(
                Ctx((longItalianLine, 0.0), ("fine", 3.5)));
            result.FailureCode.Should().Be("NARRATION_DOES_NOT_FIT", result.ErrorDetails);
            result.ErrorDetails.Should().Contain("La frase della scena 1 è troppo lunga per la scena: ha 15 parole, ma la scena dura 3.5 s e una voce può pronunciarne al massimo circa 14. Accorcia il testo o allunga la durata.");
            rig.Client.CallCount.Should().Be(0);
        }
    }

    private sealed class FakeShortener(string shorter) : INarrationLineShortener
    {
        public Task<IReadOnlyDictionary<int, string>?> ShortenAsync(IReadOnlyList<NarrationLineOverflow> lines, string? language, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<int, string>?>(lines.ToDictionary(l => l.Index, _ => shorter));
    }

    [Fact]
    public async Task The_shortener_seam_can_rescue_a_line_before_synthesis()
    {
        // 15 words in a 3.5 s window is above the hard ceiling (14), so the seam gets a chance;
        // a 10-word line at 2.86 w/s is now simply synthesized and judged on measurement.
        var rig = new Rig(3.0, 3.0, shortener: new FakeShortener(SixWords));
        StepExecutionResult result = await rig.Executor.ExecuteAsync(
            Ctx(("one two three four five six seven eight nine ten eleven twelve thirteen fourteen fifteen", 0.0), ("end", 3.5)));
        result.Status.Should().Be(StepStatus.Completed, result.ErrorDetails);
        rig.Client.Requests[0].Text.Should().Be(SixWords);
    }

    private sealed class CountingShortener(string shorter) : INarrationLineShortener
    {
        public int Calls { get; private set; }
        public Task<IReadOnlyDictionary<int, string>?> ShortenAsync(IReadOnlyList<NarrationLineOverflow> lines, string? language, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult<IReadOnlyDictionary<int, string>?>(lines.ToDictionary(l => l.Index, _ => shorter));
        }
    }

    [Fact]
    public async Task After_synthesis_a_line_the_tempo_cannot_rescue_is_shortened_once_and_only_that_line_is_resynthesized()
    {
        // The probe says every line is 5.0 s (retimed 4.6 s), so line 1 still overflows; the shortener's
        // answer is spoken for the changed line only, and the second pass fails (the probe is fixed) after ONE attempt.
        var shortener = new CountingShortener("one two three");
        var rig = new Rig(originalSec: 5.0, retimedSec: 4.6, shortener: shortener);
        StepExecutionResult result = await rig.Executor.ExecuteAsync(Ctx((SixWords, 0.0), ("seven", 3.5)));

        shortener.Calls.Should().Be(1, "one shortening attempt per batch");
        result.FailureCode.Should().Be("NARRATION_DOES_NOT_FIT");
        result.FailureClass.Should().Be(FailureClass.UserInput);
        rig.Client.Requests.Select(r => r.Text).Should().Contain("one two three");
        rig.Client.Requests.Count(r => r.Text == "seven").Should().Be(1, "unchanged lines come back from the object store");
    }

    [Fact]
    public async Task A_line_shortened_after_synthesis_is_respelled_by_the_project_glossary()
    {
        var shortener = new CountingShortener("Novara oggi");
        var glossary = new GlossaryStub([new("Novara", "Novàra", false)]);
        var rig = new Rig(originalSec: 5.0, retimedSec: 4.6, shortener: shortener, glossary: glossary);
        await rig.Executor.ExecuteAsync(Ctx((SixWords, 0.0), ("seven", 3.5)));

        rig.Client.Requests.Select(r => r.Text).Should().Contain("Novàra oggi", "the shortened line is spoken with the glossary spelling");
        rig.Client.Requests.Select(r => r.Text).Should().NotContain("Novara oggi");
    }

    private sealed class GlossaryStub(IReadOnlyList<ReelBolt.Shared.Workflows.GlossaryEntry> entries)
        : ReelBolt.WorkflowEngine.Services.Voiceover.IProjectPronunciationResolver
    {
        public Task<IReadOnlyList<ReelBolt.Shared.Workflows.GlossaryEntry>> GetEntriesAsync(Guid projectId, CancellationToken ct) =>
            Task.FromResult(entries);
    }

    [Fact]
    public async Task A_shortened_line_that_is_still_too_long_fails()
    {
        // 15 words in a 3.5 s window is above the hard ceiling (14); a 15-word "shortening" is too.
        var rig = new Rig(3.0, 3.0, shortener: new FakeShortener(
            "one two three four five six seven eight nine ten eleven twelve thirteen fourteen fifteen"));
        StepExecutionResult result = await rig.Executor.ExecuteAsync(
            Ctx(("uno due tre quattro cinque sei sette otto nove dieci undici dodici tredici quattordici quindici", 0.0), ("end", 3.5)));
        result.FailureCode.Should().Be("NARRATION_DOES_NOT_FIT", result.ErrorDetails);
        rig.Client.CallCount.Should().Be(0);
    }

    // ------------------------------------------------- measured fit: the production fixtures

    [Fact]
    public async Task The_four_production_lines_are_synthesized_and_fit_their_scenes()
    {
        // The real prod failures (window / words): 4.0 s / 10, 5.0 s / 13, 5.0 s / 11, 5.0 s / 11.
        // Each line is now spoken and judged on measured speech; all four fit their own window.
        var rig = new Rig(originalSec: 1.0, retimedSec: 1.0, durations: [4.0, 5.0, 5.0, 5.0]);
        using (RunBriefAmbient.Enter(new RunBriefState(new StoredRunBrief(LengthSec: 19))))
        {
            StepExecutionResult result = await rig.Executor.ExecuteAsync(Ctx(
                (Words(10), 0.0),
                (Words(13), 4.0),
                (Words(11), 9.0),
                // Distinct text from scene 4 on purpose: identical audio is content-addressed and the
                // second copy would be a free cache hit instead of a fourth speech call.
                ("alpha beta gamma delta epsilon zeta eta theta iota kappa lambda", 14.0)));

            result.Status.Should().Be(StepStatus.Completed, result.ErrorDetails);
            result.ErrorDetails.Should().BeNull();
            rig.Client.CallCount.Should().Be(4, "the old pre-flight gate refused these; now all four are spoken");

            JsonElement root = JsonDocument.Parse(result.Output).RootElement;
            JsonElement lines = root.GetProperty("view").GetProperty("lines");
            for (int i = 0; i < 4; i++)
                lines[i].GetProperty("fit").GetProperty("fits").GetBoolean().Should().BeTrue($"line {i} fits its declared window");
            root.GetProperty("meta").GetProperty("fit").GetProperty("overflowLineCount").GetInt32().Should().Be(0);
        }
    }

    [Fact]
    public async Task A_line_at_4_2_words_per_second_is_still_refused_before_synthesis()
    {
        // 21 words in a 5.0 s window = 4.2 words/s, above floor(5.0 x 4.0) = 20.
        var rig = new Rig(1.0, 1.0);
        StepExecutionResult result = await rig.Executor.ExecuteAsync(Ctx((Words(21), 0.0), ("end", 5.0)));

        result.FailureCode.Should().Be("NARRATION_DOES_NOT_FIT", result.ErrorDetails);
        rig.Client.CallCount.Should().Be(0, "nothing above the hard ceiling is ever synthesized");
        result.ErrorDetails.Should().Contain("about 20");
    }

    [Fact]
    public async Task A_line_that_overshoots_after_synthesis_still_fails_on_measurement()
    {
        // 10 words, 4.0 s window (the run brief's film length), probe speech 4.3 s: 0.3 s over.
        var rig = new Rig(originalSec: 1.0, retimedSec: 1.0, withRunner: false, durations: [4.3]);
        using (RunBriefAmbient.Enter(new RunBriefState(new StoredRunBrief(LengthSec: 4))))
        {
            StepExecutionResult result = await rig.Executor.ExecuteAsync(Ctx((Words(10), 0.0)));

            result.FailureCode.Should().Be("NARRATION_DOES_NOT_FIT", result.ErrorDetails);
            rig.Client.CallCount.Should().Be(1, "the line WAS synthesized: this is the measured path");
            result.ErrorDetails.Should().Contain("4.3 s of speech").And.Contain("the scene lasts 4.0 s");
        }
    }

    [Fact]
    public async Task The_tenth_of_a_second_tolerance_still_holds()
    {
        var withinTolerance = new Rig(originalSec: 1.0, retimedSec: 1.0, withRunner: false, durations: [4.05]);
        using (RunBriefAmbient.Enter(new RunBriefState(new StoredRunBrief(LengthSec: 4))))
        {
            StepExecutionResult result = await withinTolerance.Executor.ExecuteAsync(Ctx((Words(10), 0.0)));
            result.Status.Should().Be(StepStatus.Completed, result.ErrorDetails);
            JsonElement fit = JsonDocument.Parse(result.Output).RootElement
                .GetProperty("view").GetProperty("lines")[0].GetProperty("fit");
            fit.GetProperty("overflowSec").GetDouble().Should().BeLessThanOrEqualTo(NarrationFitSolver.OverflowToleranceSec);
        }

        // 0.2 s over is past the tolerance and no runner can retime it: measured failure.
        var beyondTolerance = new Rig(originalSec: 1.0, retimedSec: 1.0, withRunner: false, durations: [4.2]);
        using (RunBriefAmbient.Enter(new RunBriefState(new StoredRunBrief(LengthSec: 4))))
        {
            StepExecutionResult result = await beyondTolerance.Executor.ExecuteAsync(Ctx((Words(10), 0.0)));
            result.Status.Should().Be(StepStatus.Failed);
            result.FailureCode.Should().Be("NARRATION_DOES_NOT_FIT", result.ErrorDetails);
        }
    }

    [Fact]
    public async Task A_step_above_the_admitted_word_ceiling_fails_before_any_speech_call()
    {
        // MaxBytes bounds request bytes, not words. Two 3-word lines admit 6 words; the ceiling is 5.
        var rig = new Rig(1.0, 1.0, maxAdmittedNarrationWords: 5);
        StepExecutionResult result = await rig.Executor.ExecuteAsync(Ctx(
            ("one two three", 0.0), ("four five six", 5.0)));

        result.Status.Should().Be(StepStatus.Failed);
        result.FailureCode.Should().Be("NARRATION_TOO_MANY_WORDS", result.ErrorDetails);
        result.FailureClass.Should().Be(FailureClass.UserInput);
        result.Retryable.Should().BeFalse();
        result.ErrorDetails.Should().Contain("6 words").And.Contain("limit of 5 words");
        rig.Client.CallCount.Should().Be(0, "the spend bound is checked before any provider call");
    }

    [Fact]
    public async Task An_admitted_word_count_within_the_ceiling_is_synthesized()
    {
        var rig = new Rig(1.0, 1.0, maxAdmittedNarrationWords: 6);
        StepExecutionResult result = await rig.Executor.ExecuteAsync(Ctx(
            ("one two three", 0.0), ("four five six", 5.0)));

        result.Status.Should().Be(StepStatus.Completed, result.ErrorDetails);
    }

    [Fact]
    public async Task A_shortening_above_the_comfort_budget_is_still_refused_before_synthesis()
    {
        // The shortener may only bring a hard-ceiling line down to the 1.8 writing budget: a 10-word
        // rewrite of a 15-word line in 3.5 s (budget 6, hard ceiling 14) is rejected, so nothing is
        // spoken. Only the pre-flight ADMISSION uses the 4.0 ceiling.
        var rig = new Rig(3.0, 3.0, shortener: new FakeShortener(
            "one two three four five six seven eight nine ten"));
        StepExecutionResult result = await rig.Executor.ExecuteAsync(Ctx(
            ("one two three four five six seven eight nine ten eleven twelve thirteen fourteen fifteen", 0.0),
            ("end", 3.5)));

        result.FailureCode.Should().Be("NARRATION_DOES_NOT_FIT", result.ErrorDetails);
        rig.Client.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task The_spend_bound_is_checked_before_the_shortener_model_call()
    {
        // The step is over the ceiling AND has a physiologically impossible line. The bound must
        // refuse before the NarrationWriter shortener (itself a paid model call) is invoked.
        var shortener = new CountingShortener("Short line.");
        var rig = new Rig(1.0, 1.0, shortener: shortener, maxAdmittedNarrationWords: 5);
        StepExecutionResult result = await rig.Executor.ExecuteAsync(Ctx(
            (Words(21), 0.0), ("end", 5.0)));

        result.FailureCode.Should().Be("NARRATION_TOO_MANY_WORDS", result.ErrorDetails);
        shortener.Calls.Should().Be(0, "the bound is checked before the shortener's model call");
        rig.Client.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task A_non_positive_admitted_word_setting_falls_back_to_the_default()
    {
        var rig = new Rig(1.0, 1.0, maxAdmittedNarrationWords: 0);
        StepExecutionResult result = await rig.Executor.ExecuteAsync(Ctx(
            ("one two three", 0.0), ("four five six", 5.0)));

        result.Status.Should().Be(StepStatus.Completed, result.ErrorDetails);
    }

    [Fact]
    public async Task Report_policy_measures_only_and_keeps_the_old_shape()
    {
        var rig = new Rig(originalSec: 5.0, retimedSec: 4.6);
        StepExecutionResult result = await rig.Executor.ExecuteAsync(Ctx(NarrationFitPolicy.Report, (SixWords, 0.0), ("seven", 3.5)));

        result.Status.Should().Be(StepStatus.Completed, result.ErrorDetails);
        rig.FfmpegArgs.Should().BeEmpty();
        JsonElement root = JsonDocument.Parse(result.Output).RootElement;
        JsonElement fitLine = root.GetProperty("view").GetProperty("lines")[0].GetProperty("fit");
        fitLine.TryGetProperty("wordBudget", out _).Should().BeFalse();
        fitLine.TryGetProperty("tempo", out _).Should().BeFalse();
        JsonElement fit = root.GetProperty("meta").GetProperty("fit");
        fit.TryGetProperty("retimedLineCount", out _).Should().BeFalse();
        fit.GetProperty("overflowTotalSec").GetDouble().Should().Be(1.5);
    }

    [Fact]
    public void Fit_failure_on_user_wording_in_a_script_source_is_user_input()
    {
        VoiceoverStepExecutor.NarrationFitFailureClass(VoiceoverSourceKind.ScriptScenes, anyLineIsUserWording: true).Should().Be(FailureClass.UserInput);
        VoiceoverStepExecutor.NarrationFitFailureClass(VoiceoverSourceKind.ScriptScenes, anyLineIsUserWording: false).Should().Be(FailureClass.PlatformInternal);
    }

    [Fact]
    public void Fit_failure_table_default_is_platform_internal_and_the_source_picks_the_class()
    {
        // The static table is the platform case; the step sets an explicit class that wins over it.
        FailureClassifier.ForCode("NARRATION_DOES_NOT_FIT").Should().Be(FailureClass.PlatformInternal);
        VoiceoverStepExecutor.NarrationFitFailureClass(VoiceoverSourceKind.Inline).Should().Be(FailureClass.UserInput, "the user's own text keeps the spend");
        VoiceoverStepExecutor.NarrationFitFailureClass(VoiceoverSourceKind.ScriptScenes).Should().Be(FailureClass.PlatformInternal);
        // NarrationPlan and PickupPlan never reach the check; if they ever do, the platform wrote them.
        VoiceoverStepExecutor.NarrationFitFailureClass(VoiceoverSourceKind.NarrationPlan).Should().Be(FailureClass.PlatformInternal);
        VoiceoverStepExecutor.NarrationFitFailureClass(VoiceoverSourceKind.PickupPlan).Should().Be(FailureClass.PlatformInternal);
    }

    private static Mock<IContentModerationGate> GateBlocking(string blockedText)
    {
        var gate = new Mock<IContentModerationGate>();
        gate.Setup(g => g.ModerateAsync(It.IsAny<ModerationInput>(), It.IsAny<ModerationAttribution>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ModerationInput input, ModerationAttribution _, CancellationToken _) =>
                input.Text.Contains(blockedText, StringComparison.Ordinal)
                    ? new ModerationDecision(ModerationAction.Block, true, false, "violence", "blocked", "h")
                    : new ModerationDecision(ModerationAction.Allow, true, false, null, string.Empty, "h"));
        return gate;
    }

    [Fact]
    public async Task Content_blocked_on_text_the_shortener_wrote_is_platform_internal()
    {
        var shortener = new CountingShortener("alpha beta gamma");
        var rig = new Rig(originalSec: 5.0, retimedSec: 4.6, shortener: shortener, moderation: GateBlocking("alpha beta gamma").Object);
        StepExecutionResult result = await rig.Executor.ExecuteAsync(Ctx((SixWords, 0.0), ("seven", 3.5)));

        result.FailureCode.Should().Be("CONTENT_BLOCKED");
        result.FailureClass.Should().Be(FailureClass.PlatformInternal, "our shortener wrote it, so it is refunded");
    }

    [Fact]
    public async Task Content_blocked_on_the_users_own_text_stays_user_input()
    {
        var rig = new Rig(originalSec: 1.0, retimedSec: 1.0, moderation: GateBlocking(SixWords).Object);
        StepExecutionResult result = await rig.Executor.ExecuteAsync(Ctx((SixWords, 0.0), ("seven", 3.5)));

        result.FailureCode.Should().Be("CONTENT_BLOCKED");
        result.FailureClass.Should().Be(FailureClass.UserInput);
    }

    [Fact]
    public async Task A_refunded_fit_failure_leaves_no_line_audio_behind_so_a_rerun_pays_again()
    {
        var rig = new Rig(originalSec: 5.0, retimedSec: 4.6);
        StepExecutionResult first = await rig.Executor.ExecuteAsync(ScriptCtx((SixWords, 0), ("seven", 4)));
        first.FailureCode.Should().Be("NARRATION_DOES_NOT_FIT");
        first.FailureClass.Should().Be(FailureClass.PlatformInternal);
        int paid = rig.Client.CallCount;
        paid.Should().BeGreaterThan(0);
        rig.Store.LineArtifactKeys.Should().BeEmpty("the refunded attempt's objects are deleted, including the retimed ones");

        // Same text, wider start: with no cached audio, it must synthesize again.
        await rig.Executor.ExecuteAsync(ScriptCtx((SixWords, 0), ("seven", 5)));
        rig.Client.CallCount.Should().Be(paid * 2, "no free reuse of refunded audio");
    }

    [Fact]
    public async Task A_refunded_script_fit_failure_shows_a_specific_run_message_not_the_generic_one()
    {
        var rig = new Rig(originalSec: 5.0, retimedSec: 4.6);
        StepExecutionResult result = await rig.Executor.ExecuteAsync(ScriptCtx((SixWords, 0), ("seven", 4)));

        string message = ReelBolt.WorkflowEngine.Services.Billing.FailureMessages.RunMessage(
            new InvalidOperationException("step failed"), result.FailureClass, result.FailureCode, result.UserMessage);

        message.Should().Contain("too long for its scene").And.Contain("not charged").And.NotContain("on our side");
    }

    [Fact]
    public async Task Content_blocked_after_a_shortener_pass_also_deletes_what_the_attempt_created()
    {
        var rig = new Rig(originalSec: 5.0, retimedSec: 4.6, shortener: new CountingShortener("alpha beta gamma"),
            moderation: GateBlocking("alpha beta gamma").Object);
        StepExecutionResult result = await rig.Executor.ExecuteAsync(ScriptCtx((SixWords, 0), ("seven", 4)));
        result.FailureCode.Should().Be("CONTENT_BLOCKED");
        rig.Store.LineArtifactKeys.Should().BeEmpty();
    }

    [Fact]
    public async Task An_inline_fit_failure_keeps_its_audio_because_the_spend_is_kept()
    {
        var rig = new Rig(originalSec: 5.0, retimedSec: 4.6);
        StepExecutionResult result = await rig.Executor.ExecuteAsync(Ctx((SixWords, 0.0), ("seven", 3.5)));
        result.FailureClass.Should().Be(FailureClass.UserInput);
        rig.Store.LineArtifactKeys.Should().NotBeEmpty();
    }

    [Fact]
    public async Task A_successful_step_still_reuses_cached_lines_on_a_rerun()
    {
        var rig = new Rig(originalSec: 1.0, retimedSec: 1.0);
        (await rig.Executor.ExecuteAsync(ScriptCtx((SixWords, 0), ("seven", 4)))).ErrorDetails.Should().BeNull();
        int paid = rig.Client.CallCount;
        (await rig.Executor.ExecuteAsync(ScriptCtx((SixWords, 0), ("seven", 4)))).ErrorDetails.Should().BeNull();
        rig.Client.CallCount.Should().Be(paid);
    }

    [Fact]
    public async Task An_object_that_existed_before_a_failed_attempt_is_not_deleted()
    {
        var store = new VoiceoverCrossExecutionArtifactTests.InMemoryObjectStoreWorkspace();
        var ok = new Rig(originalSec: 1.0, retimedSec: 1.0, store: store);
        (await ok.Executor.ExecuteAsync(ScriptCtx((SixWords, 0), ("seven", 4)))).Status.Should().Be(StepStatus.Completed);
        var before = store.LineArtifactKeys.ToList();
        before.Should().NotBeEmpty();

        var failing = new Rig(originalSec: 5.0, retimedSec: 4.6, store: store);
        StepExecutionResult result = await failing.Executor.ExecuteAsync(ScriptCtx((SixWords, 0), ("seven", 4)));
        result.FailureCode.Should().Be("NARRATION_DOES_NOT_FIT");
        store.LineArtifactKeys.Should().Contain(before, "the failed attempt only removes objects it created");
    }
}
