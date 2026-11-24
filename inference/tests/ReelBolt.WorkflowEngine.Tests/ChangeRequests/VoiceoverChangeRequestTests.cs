using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.ChangeRequests;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.Replay;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Billing;
using ReelBolt.WorkflowEngine.Services.Video;
using ReelBolt.WorkflowEngine.Services.Voiceover;
using ReelBolt.WorkflowEngine.Tests.Execution.StepExecutors;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.ChangeRequests;

/// <summary>
/// A change request on a Voiceover step (docs/design/change-requests.md "Slice 1"): fake speech client, fake
/// object store, fake alignment - no paid or network call. The previous run is a real run of the same executor.
/// </summary>
public class VoiceoverChangeRequestTests
{
    private static readonly Guid ProjectId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid RequestId = Guid.Parse("12121212-1212-1212-1212-121212121212");
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static readonly string[] Texts = ["Line zero.", "Line one.", "Line two.", "Line three.", "Line four."];

    private sealed class CountingAlignment : IVoiceoverAlignmentService
    {
        public int Calls;

        public Task<VoiceoverAlignmentResult> AlignAsync(Guid projectId, string storageKey, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(VoiceoverAlignmentResult.NotApplied(VoiceoverAlignmentReasons.NoTranscriptionProvider));
        }
    }

    private sealed class FixedContext(StepChangeRequestScope? scope) : IStepChangeRequestContext
    {
        public Task<StepChangeRequestScope?> FindAsync(WorkflowExecution execution, WorkflowStep step, CancellationToken ct) =>
            Task.FromResult(scope);
    }

    private sealed class Voices(Func<Guid, ResolvedProjectVoice> resolve, HashSet<string> refused) : IProjectVoiceResolver
    {
        public Task<ResolvedProjectVoice> ResolveAsync(Guid projectId, Guid projectVoiceId, CancellationToken ct) =>
            Task.FromResult(resolve(projectVoiceId));

        public Task<IReadOnlyList<string>> FindRefusedVendorVoiceIdsAsync(Guid projectId, IReadOnlyCollection<string> remoteVoiceIds, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>([.. remoteVoiceIds.Where(refused.Contains)]);

        public Task<IReadOnlyList<ResolvedProjectVoice>> ListSelectableAsync(Guid projectId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ResolvedProjectVoice>>([]);
    }

    private sealed class Fixture
    {
        public VoiceoverCrossExecutionArtifactTests.InMemoryObjectStoreWorkspace Store { get; } = new();
        public VoiceoverCrossExecutionArtifactTests.CountingSpeechSynthesisClient Client { get; set; } = new();
        public CountingAlignment Alignment { get; set; } = new();
        public HashSet<string> RefusedVendorIds { get; } = [];
        public Func<Guid, ResolvedProjectVoice> Voice { get; set; } = id => ResolvedProjectVoice.NotFound(id);

        /// <summary>The default (Enforce) refuses a line that cannot fit its scene; most tests measure only.</summary>
        public NarrationFitPolicy Fit { get; set; } = NarrationFitPolicy.Report;

        /// <summary>the narration lines (defaults to <see cref="Texts"/>) and an optional shortener, as production DI supplies one.</summary>
        public string[] Lines { get; set; } = Texts;
        public CountingShortener? Shortener { get; set; }

        /// <summary>The per-step admitted-word ceiling; null = the production default.</summary>
        public int? MaxAdmittedNarrationWords { get; set; }

        /// <summary>The project's pronunciation glossary at the time of the run; null = no resolver wired.</summary>
        public List<GlossaryEntry>? Glossary { get; set; }
    }

    private sealed class FixedGlossary(Fixture f) : IProjectPronunciationResolver
    {
        public Task<IReadOnlyList<GlossaryEntry>> GetEntriesAsync(Guid projectId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<GlossaryEntry>>([.. f.Glossary ?? []]);
    }

    /// <summary>a deterministic stand-in for the LLM narration shortener; counts calls.</summary>
    private sealed class CountingShortener(string replacement) : INarrationLineShortener
    {
        public int Calls;

        public Task<IReadOnlyDictionary<int, string>?> ShortenAsync(IReadOnlyList<NarrationLineOverflow> lines, string? language, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult<IReadOnlyDictionary<int, string>?>(lines.ToDictionary(l => l.Index, _ => replacement));
        }
    }

    private static string ConfigJson(NarrationFitPolicy fit, string[]? lines = null) => JsonSerializer.Serialize(new VoiceoverStepConfig(
        Source: new VoiceoverSource(VoiceoverSourceKind.Inline, (lines ?? Texts).Select((t, i) => new VoiceoverLine(t, i * 2.0)).ToList()),
        ProviderId: Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
        Model: "s2.1-pro-free",
        DefaultVoiceId: "default-voice",
        MaxBytes: 2_000_000,
        NarrationFitPolicy: fit), VoiceoverStepConfig.JsonOptions);

    private static async Task<StepExecutionResult> RunAsync(Fixture f, StepChangeRequestScope? scope)
    {
        var provider = new ResolvedSpeechSynthesisProvider(
            Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"), "fish", InferenceProviderKind.FishAudio,
            "https://api.fish.audio", "s2.1-pro-free", "test-key", 30);
        var resolver = new Mock<IInferenceProviderResolver>();
        resolver.Setup(r => r.ResolveSpeechSynthesisAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>())).ReturnsAsync(provider);
        var factory = new Mock<ISpeechSynthesisClientFactory>();
        factory.Setup(x => x.Get(It.IsAny<ResolvedSpeechSynthesisProvider>())).Returns(f.Client);
        var probe = new Mock<IMediaProbe>();
        probe.Setup(p => p.ProbeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaProbeResult(1.75, 30, 1, 0, 0, null, "pcm_s16le", 44100));

        var executor = new VoiceoverStepExecutor(
            factory.Object, resolver.Object, probe.Object, f.Store, f.Alignment,
            NullLogger<VoiceoverStepExecutor>.Instance, new Voices(f.Voice, f.RefusedVendorIds), shortener: f.Shortener, changeRequests: new FixedContext(scope),
            glossaryResolver: f.Glossary is null ? null : new FixedGlossary(f),
            fitOptions: Microsoft.Extensions.Options.Options.Create(new VoiceoverFitOptions
            {
                MaxAdmittedNarrationWords = f.MaxAdmittedNarrationWords ?? VoiceoverFitOptions.DefaultMaxAdmittedNarrationWords
            }));

        var step = new WorkflowStep
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = Guid.NewGuid(),
            AgentDefinitionId = Guid.NewGuid(),
            StepOrder = 1,
            StepType = StepType.Voiceover,
            VoiceoverConfigJson = ConfigJson(f.Fit, f.Lines)
        };
        return await executor.ExecuteAsync(new StepExecutionContext
        {
            Execution = new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = ProjectId },
            Step = step,
            AllSteps = [step],
            AccumulatedOutput = string.Empty,
            StepOutputHistory = [],
            CurrentStepIndex = 0,
            IterationCount = 0,
            CorrelationId = "change-request-test",
            CancellationToken = CancellationToken.None
        });
    }

    /// <summary>The original run: 5 lines, 5 synth calls, 5 alignment calls; returns the fixture ready for the change run.</summary>
    private static async Task<(Fixture Fixture, string PreviousOutput)> OriginalRunAsync()
    {
        Fixture f = new();
        StepExecutionResult first = await RunAsync(f, null);
        first.Status.Should().Be(StepStatus.Completed);
        f.Client.CallCount.Should().Be(5);
        f.Alignment.Calls.Should().Be(5);
        f.Client = new();
        f.Alignment = new();
        return (f, first.Output!);
    }

    private static StepChangeRequestScope Scope(string previous, string[]? itemIds, string overridesJson) =>
        new(RequestId, new ChangeRequestPayload("instruction", itemIds, ParseOverrides(overridesJson)), previous);

    private static Dictionary<string, IReadOnlyDictionary<string, JsonElement>> ParseOverrides(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, JsonElement>>>(json)!
            .ToDictionary(p => p.Key, p => (IReadOnlyDictionary<string, JsonElement>)p.Value);

    private static JsonElement Line(string output, int index) =>
        JsonDocument.Parse(output).RootElement.GetProperty("view").GetProperty("lines")[index].Clone();

    [Fact]
    public async Task Changing_one_line_synthesises_exactly_one_line_and_carries_the_other_four_over_verbatim()
    {
        (Fixture f, string previous) = await OriginalRunAsync();
        StepChangeRequestScope scope = Scope(previous, ["vo2"], """{"vo2":{"text":"A brand new third line."}}""");

        StepExecutionResult result = await RunAsync(f, scope);

        result.Status.Should().Be(StepStatus.Completed);
        f.Client.CallCount.Should().Be(1);
        f.Client.Requests.Single().Text.Should().Be("A brand new third line.");
        f.Alignment.Calls.Should().Be(1);
        foreach (int unchanged in new[] { 0, 1, 3, 4 })
            Line(result.Output!, unchanged).GetRawText().Should().Be(Line(previous, unchanged).GetRawText(), $"vo{unchanged} is carried over");
        Line(result.Output!, 2).GetProperty("storageKey").GetString().Should().NotBe(Line(previous, 2).GetProperty("storageKey").GetString());
        Line(result.Output!, 2).GetProperty("text").GetString().Should().Be("A brand new third line.");

        JsonElement meta = JsonDocument.Parse(result.Output!).RootElement.GetProperty("meta").GetProperty("changeRequest");
        meta.GetProperty("requestId").GetString().Should().Be(RequestId.ToString("D"));
        meta.GetProperty("redoneLineIds").EnumerateArray().Select(e => e.GetString()).Should().Equal("vo2");
        meta.GetProperty("reusedLineCount").GetInt32().Should().Be(4);
        JsonDocument.Parse(result.Output!).RootElement.GetProperty("meta").GetProperty("totalLines").GetInt32().Should().Be(5);
    }

    [Fact]
    public async Task Only_the_changed_lines_wav_is_uploaded()
    {
        (Fixture f, string previous) = await OriginalRunAsync();
        int uploadsBefore = f.Store.UploadedKeys.Count;

        await RunAsync(f, Scope(previous, ["vo1"], """{"vo1":{"text":"Different."}}"""));

        // Only the changed line's WAV (plus the artifact summary) was uploaded.
        f.Store.UploadedKeys.Skip(uploadsBefore).Count(k => k.Contains("/voiceover/lines/", StringComparison.Ordinal)).Should().Be(1);
    }

    [Fact]
    public async Task A_voice_override_on_all_lines_synthesises_every_line_in_that_voice()
    {
        (Fixture f, string previous) = await OriginalRunAsync();

        StepExecutionResult result = await RunAsync(f, Scope(previous, null, """{"*":{"voiceId":"another-voice"}}"""));

        result.Status.Should().Be(StepStatus.Completed);
        f.Client.CallCount.Should().Be(5);
        f.Client.Requests.Should().OnlyContain(r => r.VoiceId == "another-voice");
        JsonDocument.Parse(result.Output!).RootElement.GetProperty("meta").GetProperty("changeRequest")
            .GetProperty("reusedLineCount").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task A_speed_override_changes_only_that_lines_content_key_and_reaches_the_request()
    {
        (Fixture f, string previous) = await OriginalRunAsync();

        StepExecutionResult result = await RunAsync(f, Scope(previous, ["vo1"], """{"vo1":{"speed":1.3}}"""));

        f.Client.CallCount.Should().Be(1);
        f.Client.Requests.Single().Speed.Should().Be(1.3);
        Line(result.Output!, 1).GetProperty("storageKey").GetString().Should().NotBe(Line(previous, 1).GetProperty("storageKey").GetString());
        foreach (int unchanged in new[] { 0, 2, 3, 4 })
            Line(result.Output!, unchanged).GetProperty("storageKey").GetString().Should().Be(Line(previous, unchanged).GetProperty("storageKey").GetString());
    }

    [Fact]
    public void Speed_joins_the_content_key_only_when_set()
    {
        string old = VoiceoverStepExecutor.ComputeLineContentKey("hi", "v", "m", "p", "e", null);
        VoiceoverStepExecutor.ComputeLineContentKey("hi", "v", "m", "p", "e", null, null, null).Should().Be(old);
        VoiceoverStepExecutor.ComputeLineContentKey("hi", "v", "m", "p", "e", null, null, 1.3).Should().NotBe(old);
        VoiceoverStepExecutor.ComputeLineContentKey("hi", "v", "m", "p", "e", null, null, 1.3)
            .Should().NotBe(VoiceoverStepExecutor.ComputeLineContentKey("hi", "v", "m", "p", "e", null, null, 1.4));
    }

    [Fact]
    public async Task An_unchanged_request_on_the_same_line_makes_no_speech_call()
    {
        (Fixture f, string previous) = await OriginalRunAsync();

        StepExecutionResult result = await RunAsync(f, Scope(previous, ["vo2"], "{}"));

        result.Status.Should().Be(StepStatus.Completed);
        f.Client.CallCount.Should().Be(0);
        for (int i = 0; i < 5; i++)
            Line(result.Output!, i).GetProperty("storageKey").GetString().Should().Be(Line(previous, i).GetProperty("storageKey").GetString());
    }

    [Fact]
    public async Task Override_text_goes_through_the_sanitiser()
    {
        (Fixture f, string previous) = await OriginalRunAsync();
        const string raw = "Hello (whispering) world see https://example.com/x now";

        await RunAsync(f, Scope(previous, ["vo0"], JsonSerializer.Serialize(new { vo0 = new { text = raw } })));

        string sent = f.Client.Requests.Single().Text;
        sent.Should().Be(VoiceoverTextSanitizer.Sanitize(raw, int.MaxValue));
        sent.Should().NotBe(raw).And.NotContain("https://").And.NotContain("whispering");
    }

    [Fact]
    public async Task A_voice_that_is_not_selectable_fails_and_never_falls_back()
    {
        (Fixture f, string previous) = await OriginalRunAsync();
        Guid revoked = Guid.NewGuid();
        f.Voice = id => new ResolvedProjectVoice(id, "v", null, ProjectVoiceSource.Library, null, null, false, "consent_revoked");

        StepExecutionResult result = await RunAsync(f, Scope(previous, null, JsonSerializer.Serialize(new Dictionary<string, object> { ["*"] = new { voiceId = revoked.ToString() } })));

        result.Status.Should().Be(StepStatus.Failed);
        result.ErrorDetails.Should().StartWith("VOICE_CONSENT_MISSING");
        f.Client.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task A_usable_project_voice_is_resolved_to_its_vendor_id()
    {
        (Fixture f, string previous) = await OriginalRunAsync();
        Guid voice = Guid.NewGuid();
        f.Voice = id => new ResolvedProjectVoice(id, "v", null, ProjectVoiceSource.Library, null, "vendor-xyz", true, null);

        await RunAsync(f, Scope(previous, ["vo3"], JsonSerializer.Serialize(new { vo3 = new { voiceId = voice.ToString() } })));

        f.Client.Requests.Single().VoiceId.Should().Be("vendor-xyz");
    }

    [Fact]
    public async Task A_per_line_respelling_changes_only_the_spoken_text_of_that_line()
    {
        (Fixture f, string previous) = await OriginalRunAsync();

        await RunAsync(f, Scope(previous, ["vo3"], """{"vo3":{"pronunciation":[{"term":"three","spelling":"thrèe"}]}}"""));

        SpeechRequest sent = f.Client.Requests.Single();
        sent.Text.Should().Be("Line thrèe.");
        sent.Pronunciation.Should().BeNullOrEmpty();
    }

    [Fact]
    public async Task An_invalid_respelling_fails_before_any_speech_call()
    {
        (Fixture f, string previous) = await OriginalRunAsync();

        StepExecutionResult result = await RunAsync(f, Scope(previous, ["vo3"], """{"vo3":{"pronunciation":[{"term":"three","spelling":"<b>3</b>"}]}}"""));

        result.Status.Should().Be(StepStatus.Failed);
        result.ErrorDetails.Should().StartWith("CONFIG_INVALID");
        f.Client.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task A_new_text_that_cannot_fit_its_scene_fails_clearly_before_any_speech_call()
    {
        (Fixture f, string previous) = await OriginalRunAsync();
        f.Fit = NarrationFitPolicy.Enforce;

        StepExecutionResult result = await RunAsync(f, Scope(previous, ["vo2"], JsonSerializer.Serialize(new { vo2 = new { text = string.Join(' ', Enumerable.Repeat("word", 40)) } })));

        result.Status.Should().Be(StepStatus.Failed);
        result.FailureCode.Should().Be("NARRATION_DOES_NOT_FIT");
        f.Client.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task A_change_request_line_between_the_old_threshold_and_the_hard_ceiling_is_synthesized_not_refused()
    {
        // The prod report was a change request: 6 words in a 2 s window is 3.0 words/s — above the old
        // 1.8 x 1.15 = 2.07 words/s gate, below the 4.0 words/s hard ceiling (floor(2 x 4.0) = 8).
        // Measured speech (the probe says 1.75 s) fits, so the line is spoken; owner-written text is
        // never handed to the shortener.
        (Fixture f, string previous) = await OriginalRunAsync();
        f.Fit = NarrationFitPolicy.Enforce;
        f.Shortener = new CountingShortener("Short line.");

        StepExecutionResult result = await RunAsync(f, Scope(previous, ["vo2"], """{"vo2":{"text":"one two three four five six"}}"""));

        result.Status.Should().Be(StepStatus.Completed, result.ErrorDetails);
        f.Client.CallCount.Should().Be(1);
        f.Client.Requests.Single().Text.Should().Be("one two three four five six");
        f.Shortener.Calls.Should().Be(0, "the owner's wording is never rewritten by the shortener");
        Line(result.Output!, 2).GetProperty("fit").GetProperty("fits").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Carried_over_lines_do_not_count_toward_the_admitted_word_ceiling()
    {
        // The original run has five 2-word lines (10 words). The request redoes only vo2 with 6 words,
        // so a ceiling of 6 passes only because the four carried lines are excluded from the count.
        (Fixture f, string previous) = await OriginalRunAsync();
        f.Fit = NarrationFitPolicy.Enforce;
        f.MaxAdmittedNarrationWords = 6;

        StepExecutionResult result = await RunAsync(f, Scope(previous, ["vo2"], """{"vo2":{"text":"one two three four five six"}}"""));

        result.Status.Should().Be(StepStatus.Completed, result.ErrorDetails);
        f.Client.CallCount.Should().Be(1, "only the changed line is synthesized");
    }

    [Fact]
    public async Task A_malformed_stored_request_fails_with_CHANGE_NOT_APPLIED_and_costs_nothing()
    {
        (Fixture f, string previous) = await OriginalRunAsync();

        StepExecutionResult result = await RunAsync(f, Scope(previous, null, "{}") with { Malformed = true });

        result.Status.Should().Be(StepStatus.Failed);
        result.FailureCode.Should().Be("CHANGE_NOT_APPLIED");
        result.FailureClass.Should().Be(FailureClass.UserInput);
        f.Client.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task A_request_that_matches_no_line_fails_with_CHANGE_NOT_APPLIED_and_costs_nothing()
    {
        (Fixture f, string previous) = await OriginalRunAsync();

        StepExecutionResult result = await RunAsync(f, Scope(previous, ["vo9"], """{"vo9":{"text":"Nope."}}"""));

        result.Status.Should().Be(StepStatus.Failed);
        result.FailureCode.Should().Be("CHANGE_NOT_APPLIED");
        result.FailureClass.Should().Be(FailureClass.UserInput);
        f.Client.CallCount.Should().Be(0);
        FailureClassifier.ForCode("CHANGE_NOT_APPLIED").Should().Be(FailureClass.UserInput);
    }

    [Fact]
    public async Task Named_lines_with_no_previous_output_fail_with_CHANGE_NOT_APPLIED_before_any_speech_call()
    {
        (Fixture f, _) = await OriginalRunAsync();
        StepChangeRequestScope scope = new(RequestId, new ChangeRequestPayload(
            "instruction", ["vo1"], ParseOverrides("""{"vo1":{"text":"New."}}""")), null);

        StepExecutionResult result = await RunAsync(f, scope);

        result.Status.Should().Be(StepStatus.Failed);
        result.FailureCode.Should().Be("CHANGE_NOT_APPLIED");
        result.FailureClass.Should().Be(FailureClass.UserInput);
        f.Client.CallCount.Should().Be(0, "the other lines cannot be carried over, so nothing is spoken");
        f.Alignment.Calls.Should().Be(0);
    }

    [Fact]
    public async Task A_request_on_every_line_proceeds_with_no_previous_output()
    {
        (Fixture f, _) = await OriginalRunAsync();
        StepChangeRequestScope scope = new(RequestId, new ChangeRequestPayload(
            "instruction", null, ParseOverrides("""{"*":{"speed":1.2}}""")), null);

        StepExecutionResult result = await RunAsync(f, scope);

        result.Status.Should().Be(StepStatus.Completed);
        f.Client.CallCount.Should().Be(5);
    }

    [Fact]
    public async Task A_previous_entry_that_failed_is_made_normally_instead_of_carried()
    {
        (Fixture f, string previous) = await OriginalRunAsync();
        // vo1 is out of scope but its previous entry is marked failed.
        var doc = System.Text.Json.Nodes.JsonNode.Parse(previous)!;
        doc["view"]!["lines"]![1]!["status"] = "failed";

        StepExecutionResult result = await RunAsync(f, Scope(doc.ToJsonString(), ["vo2"], """{"vo2":{"text":"Changed."}}"""));

        f.Client.CallCount.Should().Be(1, "vo1 is re-made through the normal path, whose content-addressed hit is free");
        f.Alignment.Calls.Should().Be(2, "vo1 went through the normal path (not carried) and so did vo2");
        Line(result.Output!, 1).GetProperty("status").GetString().Should().Be("ok");
        result.Status.Should().Be(StepStatus.Completed);
    }

    [Fact]
    public async Task Without_a_change_request_the_run_is_unchanged_and_carries_no_changeRequest_meta()
    {
        Fixture f = new();
        StepExecutionResult result = await RunAsync(f, null);

        JsonDocument.Parse(result.Output!).RootElement.GetProperty("meta").TryGetProperty("changeRequest", out _).Should().BeFalse();
        f.Client.CallCount.Should().Be(5);
    }

    [Fact]
    public async Task Every_ok_line_records_the_voice_it_spoke_in()
    {
        (Fixture _, string previous) = await OriginalRunAsync();
        for (int i = 0; i < 5; i++)
            Line(previous, i).GetProperty("voiceId").GetString().Should().Be("default-voice");
    }

    [Fact]
    public async Task A_carried_line_whose_voice_is_still_live_is_carried()
    {
        (Fixture f, string previous) = await OriginalRunAsync();

        StepExecutionResult result = await RunAsync(f, Scope(previous, ["vo2"], "{}"));

        result.Status.Should().Be(StepStatus.Completed);
        Line(result.Output!, 0).GetRawText().Should().Be(Line(previous, 0).GetRawText());
    }

    [Fact]
    public async Task A_carried_line_whose_voice_consent_was_revoked_or_is_foreign_fails_with_VOICE_CONSENT_MISSING_and_no_speech_call()
    {
        (Fixture f, string previous) = await OriginalRunAsync();
        f.RefusedVendorIds.Add("default-voice"); // revoked, deleted or another project's clone: the shared rule refuses it

        StepExecutionResult result = await RunAsync(f, Scope(previous, ["vo2"], """{"vo2":{"text":"Changed."}}"""));

        result.Status.Should().Be(StepStatus.Failed);
        result.ErrorDetails.Should().StartWith("VOICE_CONSENT_MISSING");
        f.Client.CallCount.Should().Be(0);
        f.Alignment.Calls.Should().Be(0);
    }

    [Fact]
    public async Task A_previous_output_without_voiceId_is_derived_from_meta_or_the_line_is_made_again()
    {
        (Fixture f, string previous) = await OriginalRunAsync();
        var stripped = System.Text.Json.Nodes.JsonNode.Parse(previous)!;
        foreach (var line in stripped["view"]!["lines"]!.AsArray())
            line!.AsObject().Remove("voiceId");

        StepExecutionResult derived = await RunAsync(f, Scope(stripped.ToJsonString(), ["vo2"], "{}"));
        derived.Status.Should().Be(StepStatus.Completed);
        f.Alignment.Calls.Should().Be(1, "only vo2 went through the normal path");

        stripped["meta"]!.AsObject().Remove("voice");
        f.Alignment = new();
        StepExecutionResult underivable = await RunAsync(f, Scope(stripped.ToJsonString(), ["vo2"], "{}"));
        underivable.Status.Should().Be(StepStatus.Completed);
        f.Alignment.Calls.Should().Be(5, "no line could be vouched for, so none was carried");
        f.Client.CallCount.Should().Be(0, "the content-addressed hits are free");
    }

    // ------------------------------------------------------------------ shortener interplay

    [Fact]
    public async Task An_override_text_that_cannot_fit_fails_even_when_a_shortener_is_wired_and_is_never_rewritten()
    {
        // A new text is refused before synthesis only when physically impossible: 40 words in a 2 s window
        // is above the 4.0 words/s hard ceiling (floor(8) = 8). Production DI registers
        // INarrationLineShortener, so the owner's own wording must not be rewritten by the model.
        (Fixture f, string previous) = await OriginalRunAsync();
        f.Fit = NarrationFitPolicy.Enforce;
        f.Shortener = new CountingShortener("Short line.");

        StepExecutionResult result = await RunAsync(f, Scope(previous, ["vo2"], JsonSerializer.Serialize(new { vo2 = new { text = string.Join(' ', Enumerable.Repeat("word", 40)) } })));

        f.Shortener.Calls.Should().Be(0, "the owner's override text is not handed to the shortener");
        result.FailureCode.Should().Be("NARRATION_DOES_NOT_FIT");
        f.Client.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task A_line_the_original_run_shortened_is_carried_over_not_reshortened_and_remade()
    {
        // Original run (Enforce, shortener wired): vo0 is 12 words in a 2 s window (hard ceiling 8), so it is
        // shortened before synthesis and the output records the SHORTENED text. A change on vo2 must carry vo0 verbatim.
        Fixture f = new()
        {
            Fit = NarrationFitPolicy.Enforce,
            Lines = ["One two three four five six seven eight nine ten eleven twelve.", "Line one.", "Line two.", "Line three.", "Line four."],
            Shortener = new CountingShortener("Short one.")
        };
        StepExecutionResult first = await RunAsync(f, null);
        first.Status.Should().Be(StepStatus.Completed);
        Line(first.Output!, 0).GetProperty("text").GetString().Should().Be("Short one.");
        f.Shortener = new CountingShortener("Short one.");
        f.Client = new();
        f.Alignment = new();

        StepExecutionResult result = await RunAsync(f, Scope(first.Output!, ["vo2"], """{"vo2":{"text":"Changed."}}"""));

        result.Status.Should().Be(StepStatus.Completed);
        f.Shortener.Calls.Should().Be(0, "no line outside the request needs the model again");
        f.Alignment.Calls.Should().Be(1, "only vo2 is re-made");
        Line(result.Output!, 0).GetRawText().Should().Be(Line(first.Output!, 0).GetRawText(), "vo0 is carried over");
    }

    [Fact]
    public async Task New_wording_that_is_empty_once_cleaned_fails_before_any_speech_call()
    {
        (Fixture f, string previous) = await OriginalRunAsync();

        StepExecutionResult result = await RunAsync(f, Scope(previous, ["vo2"], """{"vo2":{"text":"https://example.com/only-a-link"}}"""));

        result.Status.Should().Be(StepStatus.Failed);
        result.ErrorDetails.Should().StartWith("CONFIG_INVALID");
        f.Client.CallCount.Should().Be(0);
    }

    [Fact]
    public void A_line_speed_outside_the_range_is_refused_on_every_run()
    {
        var config = new VoiceoverStepConfig(
            Source: new VoiceoverSource(VoiceoverSourceKind.Inline, [new VoiceoverLine("Hi.", 0, Speed: 9)]));

        config.Validate().Should().ContainSingle().Which.Should().Contain("speed");
    }

    private static readonly string[] NovaraLines =
        ["Welcome to Novara.", "A quiet second line.", "Novara again at night.", "Nothing special here.", "The end."];

    private static async Task<(Fixture Fixture, string PreviousOutput)> RunWithoutGlossaryAsync()
    {
        Fixture f = new() { Lines = NovaraLines };
        StepExecutionResult first = await RunAsync(f, null);
        first.Status.Should().Be(StepStatus.Completed);
        f.Client = new();
        f.Alignment = new();
        return (f, first.Output!);
    }

    [Fact]
    public async Task A_pronunciation_saved_after_the_run_is_used_when_a_change_request_redoes_other_lines()
    {
        (Fixture f, string previous) = await RunWithoutGlossaryAsync();
        f.Glossary = [new GlossaryEntry("Novara", "Novàra", false)];
        StepChangeRequestScope scope = Scope(previous, ["vo1"], """{"vo1":{"speed":1.1}}""");

        StepExecutionResult result = await RunAsync(f, scope);

        result.Status.Should().Be(StepStatus.Completed);
        // vo1 was named; vo0 and vo2 hold the term and were NOT named: they are made again with the new spelling.
        f.Client.Requests.Select(r => r.Text).Should().BeEquivalentTo(
            "Welcome to Novàra.", "A quiet second line.", "Novàra again at night.");
        Line(result.Output!, 0).GetProperty("spoken").GetString().Should().Be("Welcome to Novàra.");
        Line(result.Output!, 0).GetProperty("text").GetString().Should().Be("Welcome to Novara.", "captions keep the written words");
        foreach (int unchanged in new[] { 3, 4 })
            Line(result.Output!, unchanged).GetRawText().Should().Be(Line(previous, unchanged).GetRawText(), $"vo{unchanged} has no glossary term");
        JsonDocument.Parse(result.Output!).RootElement.GetProperty("meta").GetProperty("changeRequest")
            .GetProperty("redoneLineIds").EnumerateArray().Select(e => e.GetString())
            .Should().BeEquivalentTo("vo0", "vo1", "vo2");
    }

    [Fact]
    public async Task A_line_that_was_already_spoken_with_the_current_pronunciation_is_carried_over()
    {
        Fixture f = new() { Lines = NovaraLines, Glossary = [new GlossaryEntry("Novara", "Novàra", false)] };
        StepExecutionResult first = await RunAsync(f, null);
        first.Status.Should().Be(StepStatus.Completed);
        f.Client = new();
        f.Alignment = new();

        StepExecutionResult result = await RunAsync(f, Scope(first.Output!, ["vo3"], """{"vo3":{"speed":1.1}}"""));

        result.Status.Should().Be(StepStatus.Completed);
        f.Client.CallCount.Should().Be(1, "only the named line is made; the glossary did not change");
    }

    [Fact]
    public async Task A_pronunciation_edited_after_the_run_remakes_the_untouched_lines_that_hold_the_term()
    {
        Fixture f = new() { Lines = NovaraLines, Glossary = [new GlossaryEntry("Novara", "Novàra", false)] };
        StepExecutionResult first = await RunAsync(f, null);
        f.Client = new();
        f.Alignment = new();
        f.Glossary = [new GlossaryEntry("Novara", "Nòvara", false)];

        StepExecutionResult result = await RunAsync(f, Scope(first.Output!, ["vo3"], """{"vo3":{"speed":1.1}}"""));

        f.Client.Requests.Select(r => r.Text).Should().BeEquivalentTo("Welcome to Nòvara.", "Novara again at night.".Replace("Novara", "Nòvara"), "Nothing special here.");
        Line(result.Output!, 2).GetProperty("spoken").GetString().Should().Be("Nòvara again at night.");
    }

    [Fact]
    public async Task A_pronunciation_removed_after_the_run_remakes_the_lines_that_had_it()
    {
        Fixture f = new() { Lines = NovaraLines, Glossary = [new GlossaryEntry("Novara", "Novàra", false)] };
        StepExecutionResult first = await RunAsync(f, null);
        f.Client = new();
        f.Alignment = new();
        f.Glossary = [];

        StepExecutionResult result = await RunAsync(f, Scope(first.Output!, ["vo3"], """{"vo3":{"speed":1.1}}"""));

        f.Client.Requests.Select(r => r.Text).Should().BeEquivalentTo(
            "Welcome to Novara.", "Novara again at night.", "Nothing special here.");
        Line(result.Output!, 0).TryGetProperty("spoken", out _).Should().BeFalse("it is spoken as written again");
    }

    [Fact]
    public async Task A_line_given_its_own_pronunciation_by_an_earlier_request_is_not_remade_by_the_next_one()
    {
        Fixture f = new() { Lines = NovaraLines, Glossary = [new GlossaryEntry("Novara", "Novàra", false)] };
        StepExecutionResult first = await RunAsync(f, null);
        f.Client = new();
        f.Alignment = new();
        StepExecutionResult second = await RunAsync(
            f, Scope(first.Output!, ["vo0"], """{"vo0":{"pronunciation":[{"term":"Novara","spelling":"Nòvara"}]}}"""));
        second.Status.Should().Be(StepStatus.Completed);
        f.Client.Requests.Should().ContainSingle().Which.Text.Should().Be("Welcome to Nòvara.");
        f.Client = new();
        f.Alignment = new();

        StepExecutionResult third = await RunAsync(f, Scope(second.Output!, ["vo3"], """{"vo3":{"speed":1.1}}"""));

        third.Status.Should().Be(StepStatus.Completed);
        f.Client.CallCount.Should().Be(1, "the glossary did not change, and vo0 keeps the audio made with its own spelling");
    }
}
