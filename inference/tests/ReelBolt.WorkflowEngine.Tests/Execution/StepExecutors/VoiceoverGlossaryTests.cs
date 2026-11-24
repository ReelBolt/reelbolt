using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.Caching;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Video;
using ReelBolt.WorkflowEngine.Services.Voiceover;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Execution.StepExecutors;

/// <summary>The project pronunciation glossary: written-accent respellings in the TTS text only.</summary>
public class PronunciationRespellerTests
{
    private static GlossaryEntry E(string term, string spelling, bool cs = false) => new(term, spelling, cs);

    [Fact]
    public void Respells_whole_words_and_leaves_longer_words_alone()
    {
        var (text, applied) = PronunciationRespeller.Apply(
            "Lo studio di Novara offre garanzie complete. I novaresi no, Novarese no.",
            [E("Novara", "Novàra"), E("garanzie", "garanzìe")]);
        text.Should().Be("Lo studio di Novàra offre garanzìe complete. I novaresi no, Novarese no.");
        applied.Select(a => (a.Term, a.Count)).Should().Equal(("Novara", 1), ("garanzie", 1));
    }

    [Fact]
    public void Case_insensitive_entries_adapt_the_first_letter_and_all_caps()
    {
        PronunciationRespeller.Apply("Novara NOVARA novara", [E("Novara", "Novàra")]).Text
            .Should().Be("Novàra NOVÀRA novàra");
    }

    [Fact]
    public void Case_sensitive_entries_match_exactly()
    {
        PronunciationRespeller.Apply("Novara novara", [E("Novara", "Novàra", cs: true)]).Text
            .Should().Be("Novàra novara");
    }

    [Fact]
    public void Longest_term_wins_and_replacements_are_not_rematched()
    {
        PronunciationRespeller.Apply("Novara Est", [E("Novara", "Novàra"), E("Novara Est", "Novàra Èst"), E("Novàra", "X")]).Text
            .Should().Be("Novàra Èst");
        PronunciationRespeller.Apply("Novara", [E("Novara", "Novàra"), E("Novàra", "Novara")]).Text.Should().Be("Novàra");
    }

    [Fact]
    public void Decomposed_input_is_normalised_and_unicode_boundaries_hold()
    {
        string decomposed = "Città di Novara".Normalize(NormalizationForm.FormD);
        PronunciationRespeller.Apply(decomposed, [E("Novara", "Novàra")]).Text.Should().Be("Città di Novàra");
        // a letter with a combining accent next to the term is part of a longer word
        PronunciationRespeller.Apply("Novarà Novara1 Novara,", [E("Novara", "Novàra")]).Text.Should().Be("Novarà Novara1 Novàra,");
    }

    [Fact]
    public void Empty_glossary_returns_the_text_unchanged_and_digest_is_null()
    {
        PronunciationRespeller.Apply("Ciao Novara", []).Text.Should().Be("Ciao Novara");
        PronunciationRespeller.Digest([]).Should().BeNull();
        PronunciationRespeller.Digest([E("abc", "àbc")]).Should().NotBeNull()
            .And.NotBe(PronunciationRespeller.Digest([E("abc", "ábc")]));
    }

    [Fact]
    public void Only_the_first_200_entries_are_used()
    {
        static string Word(int i) => "w" + new string(new[] { (char)('a' + (i / 26 % 26)), (char)('a' + (i % 26)) });
        List<GlossaryEntry> many = Enumerable.Range(0, 201).Select(i => E(Word(i), Word(i) + "h")).ToList();
        PronunciationRespeller.Apply($"{Word(200)} {Word(0)}", many).Text.Should().Be($"{Word(200)} {Word(0)}h");
    }

    [Theory]
    [InlineData("Novara", "Novara1")]
    [InlineData("Novara", "novara.com")]
    [InlineData("Novara", "<b>Novara</b>")]
    [InlineData("Novara", "Novàra Novàra Novàra Novàra")]
    public void An_entry_the_save_rules_would_refuse_is_never_substituted(string term, string spelling)
    {
        PronunciationRespeller.Apply($"Dico {term} ora", [E(term, spelling)]).Text.Should().Be($"Dico {term} ora");
        PronunciationRespeller.Digest([E(term, spelling)]).Should().BeNull("an unusable entry adds nothing to a cache key");
    }

    [Fact]
    public void A_respelling_may_change_letters()
    {
        PronunciationRespeller.Apply("Compra Xiaomi", [E("Xiaomi", "Sciaomi")]).Text.Should().Be("Compra Sciaomi");
    }

    [Fact]
    public void Step_cache_key_is_unchanged_without_a_glossary_and_changes_with_one()
    {
        var builder = new StepCacheKeyBuilder();
        StepCacheKeyInputs Inputs(string? glossary) => new()
        {
            ProjectId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            StepType = StepType.Voiceover,
            AgentType = default,
            AgentDefinitionId = Guid.Empty,
            ProjectFileFingerprint = "fp",
            AgentInputContextMode = default,
            MaxIterations = 1,
            VoiceoverConfigJson = "{}",
            ProjectPronunciation = glossary
        };
        builder.Build(Inputs(null)).Should().Be(builder.Build(Inputs(null)));
        builder.Build(Inputs("abc")).Should().NotBe(builder.Build(Inputs(null)));
        builder.Build(Inputs("abc")).Should().NotBe(builder.Build(Inputs("abd")));
    }
}

public partial class VoiceoverStepExecutorTests
{
    private sealed class FakeGlossary : IProjectPronunciationResolver
    {
        public List<GlossaryEntry> Entries { get; init; } = [];
        public bool Fail { get; init; }

        public Task<IReadOnlyList<GlossaryEntry>> GetEntriesAsync(Guid projectId, CancellationToken ct) =>
            Fail ? throw new InvalidOperationException("db down") : Task.FromResult<IReadOnlyList<GlossaryEntry>>(Entries);
    }

    private static async Task<(List<SpeechRequest> Requests, List<string> Keys, StepExecutionResult Result)> RunGlossary(
        VoiceoverFakes fakes,
        IProjectPronunciationResolver? glossary,
        string[] lines,
        IReadOnlyList<PronunciationOverride>? overrides = null,
        ReelBolt.Shared.Moderation.IContentModerationGate? moderation = null,
        int maxBytes = 2_000_000)
    {
        List<string> keys = [];
        List<SpeechRequest> requests = [];
        SetupFreshValidWavPerCall(fakes);
        fakes.MediaProbe
            .Setup(p => p.ProbeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaProbeResult(1.0, 0, 0, 0, 0, null, "pcm_s16le", 44100));
        fakes.Workspace
            .Setup(w => w.ArtifactExistsAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        fakes.Workspace
            .Setup(w => w.UploadArtifactAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.Is<string>(k => k.EndsWith(".wav")), "audio/wav", It.IsAny<CancellationToken>(), It.IsAny<string>()))
            .Callback<Guid, string, string, string, CancellationToken, string>((_, _, rel, _, _, _) => keys.Add(rel))
            .ReturnsAsync((Guid _, string _, string relativePath, string _, CancellationToken _, string _) =>
                $"projects/{ProjectId}/agentFiles/{relativePath}");
        fakes.Client
            .Setup(c => c.SynthesizeAsync(It.IsAny<SpeechRequest>(), It.IsAny<CancellationToken>()))
            .Callback<SpeechRequest, CancellationToken>((r, _) => requests.Add(r))
            .Returns((SpeechRequest _, CancellationToken _) =>
                Task.FromResult(new SynthesizedSpeech(new MemoryStream(ValidWavBytes()), "audio/wav")));

        string config = SerializeConfig(new VoiceoverStepConfig(
            Source: new VoiceoverSource(VoiceoverSourceKind.Inline, lines.Select((t, i) => new VoiceoverLine(t, i * 5.0)).ToArray()),
            ProviderId: Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Model: "test-model",
            DefaultVoiceId: "default-voice",
            MaxBytes: maxBytes,
            PronunciationOverrides: overrides));
        var executor = new VoiceoverStepExecutor(
            fakes.SpeechFactory.Object, fakes.ProviderResolver.Object, fakes.MediaProbe.Object, fakes.Workspace.Object,
            new StubAlignmentService(), NullLogger<VoiceoverStepExecutor>.Instance,
            voiceResolver: new NoRowsVoiceResolver(), glossaryResolver: glossary, moderation: moderation);
        StepExecutionResult result = await executor.ExecuteAsync(CreateContext(config));
        return (requests, keys, result);
    }

    [Fact]
    public async Task Glossary_respells_only_the_text_sent_to_the_provider()
    {
        VoiceoverFakes fakes = CreateFakes();
        var glossary = new FakeGlossary { Entries = [new("Novara", "Novàra", false), new("garanzie", "garanzìe", false)] };
        var run = await RunGlossary(fakes, glossary, ["Lo studio di Novara offre garanzie complete."]);

        run.Result.Status.Should().Be(StepStatus.Completed, because: run.Result.ErrorDetails ?? run.Result.Output);
        run.Requests.Should().ContainSingle();
        run.Requests[0].Text.Should().Be("Lo studio di Novàra offre garanzìe complete.");
        run.Requests[0].Pronunciation.Should().BeNull("glossary entries never go through the provider dictionary");

        JsonElement root = ParseOutput(run.Result);
        root.GetRawText().Should().Contain("Lo studio di Novara offre garanzie complete.", "the saved line text keeps the author's words");
        root.GetRawText().Should().NotContain("Novàra");
        JsonElement meta = root.GetProperty("meta").GetProperty("pronunciation");
        meta.GetProperty("glossaryCount").GetInt32().Should().Be(2);
        meta.GetProperty("glossaryApplied").GetArrayLength().Should().Be(2);
        meta.GetProperty("glossaryDigest").GetString().Should().Be(PronunciationRespeller.Digest(glossary.Entries), "the run service files the result under the digest the step used");
    }

    [Fact]
    public async Task Empty_glossary_sends_a_byte_identical_request_and_key_and_a_changed_glossary_touches_only_its_lines()
    {
        VoiceoverFakes fakes = CreateFakes();
        string[] lines = ["Ciao a tutti.", "Siamo a Novara."];
        var none = await RunGlossary(fakes, null, lines);
        var empty = await RunGlossary(fakes, new FakeGlossary(), lines);
        var with = await RunGlossary(fakes, new FakeGlossary { Entries = [new("Novara", "Novàra", false)] }, lines);

        none.Requests.Should().BeEquivalentTo(empty.Requests);
        none.Keys.Should().Equal(empty.Keys);
        with.Requests[1].Text.Should().Be("Siamo a Novàra.");
        with.Keys[0].Should().Be(none.Keys[0], "a line with no match keeps its key and cached object");
        with.Keys[1].Should().NotBe(none.Keys[1]);
    }

    [Fact]
    public async Task Step_override_wins_over_a_glossary_entry_for_the_same_term()
    {
        VoiceoverFakes fakes = CreateFakes();
        var glossary = new FakeGlossary { Entries = [new("Novara", "Novàra", false), new("garanzie", "garanzìe", false)] };
        var run = await RunGlossary(fakes, glossary, ["Novara offre garanzie."], [new PronunciationOverride("novara", "noˈvaːra")]);

        run.Requests[0].Text.Should().Be("Novara offre garanzìe.", "the overridden term is left to the step override");
        run.Requests[0].Pronunciation.Should().ContainSingle();
    }

    [Fact]
    public async Task An_unreadable_glossary_fails_the_step_without_speaking()
    {
        VoiceoverFakes fakes = CreateFakes();
        var run = await RunGlossary(fakes, new FakeGlossary { Fail = true }, ["Siamo a Novara."]);

        run.Result.Status.Should().Be(StepStatus.Failed);
        run.Result.ErrorDetails.Should().StartWith("GLOSSARY_UNAVAILABLE");
        run.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Moderation_screens_the_respelled_text_that_is_spoken_and_the_written_text()
    {
        VoiceoverFakes fakes = CreateFakes();
        var gate = new FakeGate(ReelBolt.Shared.Moderation.ModerationAction.Allow);
        var glossary = new FakeGlossary { Entries = [new("Novara", "Novàra", false)] };
        var run = await RunGlossary(fakes, glossary, ["Siamo a Novara."], moderation: gate);

        run.Result.Status.Should().Be(StepStatus.Completed, because: run.Result.ErrorDetails ?? run.Result.Output);
        gate.Inputs.Select(i => i.Text).Should().Contain(t => t.Contains("Novàra"), "the text the provider speaks is the text that is screened");
        gate.Inputs.Select(i => i.Text).Should().Contain(t => t.Contains("Novara"), "the written narration is screened as well");
        run.Requests[0].Text.Should().Be("Siamo a Novàra.");
    }

    [Fact]
    public async Task Moderation_blocking_the_respelled_text_stops_the_step_before_synthesis()
    {
        VoiceoverFakes fakes = CreateFakes();
        var gate = new BlockWhenContainsGate("Novàra");
        var glossary = new FakeGlossary { Entries = [new("Novara", "Novàra", false)] };
        var run = await RunGlossary(fakes, glossary, ["Siamo a Novara."], moderation: gate);

        run.Result.Status.Should().Be(StepStatus.Failed);
        run.Result.ErrorDetails.Should().StartWith("CONTENT_BLOCKED");
        run.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task The_byte_budget_is_judged_on_the_respelled_text()
    {
        VoiceoverFakes fakes = CreateFakes();
        // "Novara" is 6 bytes written and 7 bytes once respelled (à is two bytes in UTF-8).
        var glossary = new FakeGlossary { Entries = [new("Novara", "Novàra", false)] };
        var within = await RunGlossary(fakes, null, ["Novara"], maxBytes: 6);
        within.Result.Status.Should().Be(StepStatus.Completed, because: within.Result.ErrorDetails ?? within.Result.Output);

        var over = await RunGlossary(fakes, glossary, ["Novara"], maxBytes: 6);
        over.Result.Status.Should().Be(StepStatus.Failed);
        over.Result.ErrorDetails.Should().StartWith("BYTE_BUDGET_EXCEEDED");
        over.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_stored_entry_the_save_rules_would_refuse_is_not_spoken()
    {
        VoiceoverFakes fakes = CreateFakes();
        var glossary = new FakeGlossary { Entries = [new("Novara", "Novara dot com 123", false)] };
        var run = await RunGlossary(fakes, glossary, ["Siamo a Novara."]);

        run.Requests[0].Text.Should().Be("Siamo a Novara.");
    }

    [Fact]
    public async Task A_respelling_that_changes_letters_is_screened_as_spoken_and_blocks_the_step()
    {
        VoiceoverFakes fakes = CreateFakes();
        var gate = new BlockWhenContainsGate("Nowara");
        var glossary = new FakeGlossary { Entries = [new("Novara", "Nowara", false)] };
        var run = await RunGlossary(fakes, glossary, ["Siamo a Novara."], moderation: gate);

        run.Result.Status.Should().Be(StepStatus.Failed);
        run.Result.ErrorDetails.Should().StartWith("CONTENT_BLOCKED");
        run.Requests.Should().BeEmpty();
    }

    private sealed class BlockWhenContainsGate(string needle) : ReelBolt.Shared.Moderation.IContentModerationGate
    {
        public Task<ReelBolt.Shared.Moderation.ModerationDecision> ModerateAsync(
            ReelBolt.Shared.Moderation.ModerationInput input, ReelBolt.Shared.Moderation.ModerationAttribution attribution, CancellationToken ct)
        {
            var action = input.Text.Contains(needle, StringComparison.Ordinal)
                ? ReelBolt.Shared.Moderation.ModerationAction.Block
                : ReelBolt.Shared.Moderation.ModerationAction.Allow;
            return Task.FromResult(new ReelBolt.Shared.Moderation.ModerationDecision(action, true, false, null, "blocked", "hash"));
        }

        public Task<bool> ScreenGenerationPromptAsync(string text, IReadOnlyList<ReelBolt.Shared.Moderation.ModerationImageInput> images, ReelBolt.Shared.Moderation.ModerationAttribution attribution, CancellationToken ct) => Task.FromResult(true);
        public Task<bool> ScreenGeneratedTextAsync(string text, string site, ReelBolt.Shared.Moderation.ModerationAttribution attribution, CancellationToken ct) => Task.FromResult(true);
        public Task<bool> ScreenPublishAsync(ReelBolt.Shared.Moderation.ModerationInput input, ReelBolt.Shared.Moderation.ModerationAttribution attribution, CancellationToken ct) => Task.FromResult(true);
    }
}
