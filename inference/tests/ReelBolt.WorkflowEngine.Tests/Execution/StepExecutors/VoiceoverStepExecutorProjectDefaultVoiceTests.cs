using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using Moq;
using ReelBolt.Shared.Inference;
using ReelBolt.WorkflowEngine.Execution.Decision;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.Caching;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Voiceover;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Execution.StepExecutors;

/// <summary>
/// The project's one selectable voice is the default narrator (docs/voiceover.md "The project's one voice is the
/// default"): one selectable voice — Cloned or Library — speaks when the step names none, several are a clear failure,
/// a named voice keeps winning, a project without one is unchanged; the default is part of the step-cache key; a
/// replayed narration carries no stale provider-health warning. No provider is called: the speech client is the fake.
/// </summary>
public partial class VoiceoverStepExecutorTests
{
    private static ResolvedProjectVoice Clone(Guid id, string name, string remote, bool usable = true) =>
        new(id, name, null, ProjectVoiceSource.Cloned, Guid.NewGuid(), usable ? remote : null, usable, usable ? null : "consent_revoked");

    /// <summary>A step that names no voice at all: no project voice, no default voice id.</summary>
    private static string UnvoicedConfigJson() =>
        SerializeConfig(new VoiceoverStepConfig(
            Source: new VoiceoverSource(VoiceoverSourceKind.Inline, new[] { new VoiceoverLine("Hello there.", 0.0) }),
            ProviderId: Guid.NewGuid(),
            Model: "test-model",
            MaxBytes: 2_000_000));

    [Fact]
    public async Task Exactly_one_selectable_clone_narrates_when_the_step_names_no_voice()
    {
        VoiceoverFakes fakes = CreateFakes();
        List<SpeechRequest> requests = [];
        SetupSuccessfulSynthesis(fakes, requests);
        var resolver = new StubVoiceResolver();
        Guid cloneId = Guid.NewGuid();
        resolver.Voices[cloneId] = Clone(cloneId, "My voice", "fish-clone-1");
        Guid revoked = Guid.NewGuid();
        resolver.Voices[revoked] = Clone(revoked, "Old voice", "fish-old", usable: false);

        StepExecutionResult result = await CreateExecutorWithVoices(fakes, resolver).ExecuteAsync(CreateContext(UnvoicedConfigJson()));

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        requests.Should().ContainSingle().Which.VoiceId.Should().Be("fish-clone-1", "the only usable clone, never the provider default");
        JsonElement voice = ParseOutput(result).GetProperty("meta").GetProperty("voice");
        voice.GetProperty("projectVoiceId").GetGuid().Should().Be(cloneId);
        voice.GetProperty("source").GetString().Should().Be("project_voice");
        voice.GetProperty("defaulted").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Several_selectable_clones_and_no_named_voice_fail_with_an_actionable_message_and_speak_nothing()
    {
        VoiceoverFakes fakes = CreateFakes();
        List<SpeechRequest> requests = [];
        SetupSuccessfulSynthesis(fakes, requests);
        var resolver = new StubVoiceResolver();
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        resolver.Voices[a] = Clone(a, "Anna", "fish-a");
        resolver.Voices[b] = Clone(b, "Bruno", "fish-b");

        StepExecutionResult result = await CreateExecutorWithVoices(fakes, resolver).ExecuteAsync(CreateContext(UnvoicedConfigJson()));

        result.Status.Should().Be(StepStatus.Failed);
        result.ErrorDetails.Should().StartWith("VOICE_CHOICE_REQUIRED");
        result.ErrorDetails.Should().Contain("2 voices").And.NotContain("cloned voices").And.Contain("Anna").And.Contain("Bruno").And.Contain("voice setting");
        result.Retryable.Should().BeFalse("choosing a voice is the person's, a retry fails identically");
        requests.Should().BeEmpty("the provider's stock voice must not quietly narrate while the project owns clones");
    }

    [Fact]
    public async Task A_named_project_voice_wins_over_the_default_even_with_other_clones()
    {
        VoiceoverFakes fakes = CreateFakes();
        List<SpeechRequest> requests = [];
        SetupSuccessfulSynthesis(fakes, requests);
        var resolver = new StubVoiceResolver();
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        resolver.Voices[a] = Clone(a, "Anna", "fish-a");
        resolver.Voices[b] = Clone(b, "Bruno", "fish-b");
        string json = SerializeConfig(new VoiceoverStepConfig(
            Source: new VoiceoverSource(VoiceoverSourceKind.Inline, new[] { new VoiceoverLine("Hello there.", 0.0) }),
            ProviderId: Guid.NewGuid(), Model: "test-model", MaxBytes: 2_000_000, ProjectVoiceId: b));

        StepExecutionResult result = await CreateExecutorWithVoices(fakes, resolver).ExecuteAsync(CreateContext(json));

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        requests.Should().ContainSingle().Which.VoiceId.Should().Be("fish-b");
        JsonElement voice = ParseOutput(result).GetProperty("meta").GetProperty("voice");
        voice.TryGetProperty("defaulted", out _).Should().BeFalse("a named voice is not a defaulted one");
    }

    [Fact]
    public async Task A_named_voice_that_is_unusable_still_fails_and_never_falls_back_to_the_only_other_clone()
    {
        VoiceoverFakes fakes = CreateFakes();
        List<SpeechRequest> requests = [];
        SetupSuccessfulSynthesis(fakes, requests);
        var resolver = new StubVoiceResolver();
        Guid good = Guid.NewGuid(), revoked = Guid.NewGuid();
        resolver.Voices[good] = Clone(good, "Anna", "fish-a");
        resolver.Voices[revoked] = Clone(revoked, "Bruno", "fish-b", usable: false);
        string json = SerializeConfig(new VoiceoverStepConfig(
            Source: new VoiceoverSource(VoiceoverSourceKind.Inline, new[] { new VoiceoverLine("Hello there.", 0.0) }),
            ProviderId: Guid.NewGuid(), Model: "test-model", MaxBytes: 2_000_000, ProjectVoiceId: revoked));

        StepExecutionResult result = await CreateExecutorWithVoices(fakes, resolver).ExecuteAsync(CreateContext(json));

        result.Status.Should().Be(StepStatus.Failed);
        result.ErrorDetails.Should().StartWith("VOICE_CONSENT_MISSING");
        requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_step_with_its_own_default_voice_id_keeps_it_over_the_project_clone()
    {
        VoiceoverFakes fakes = CreateFakes();
        List<SpeechRequest> requests = [];
        SetupSuccessfulSynthesis(fakes, requests);
        var resolver = new StubVoiceResolver();
        Guid cloneId = Guid.NewGuid();
        resolver.Voices[cloneId] = Clone(cloneId, "My voice", "fish-clone-1");

        StepExecutionResult result = await CreateExecutorWithVoices(fakes, resolver).ExecuteAsync(CreateContext(OneLineConfigJson()));

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        requests.Should().ContainSingle().Which.VoiceId.Should().Be("default-voice", "the step named a voice of its own");
    }

    [Fact]
    public async Task Exactly_one_selectable_library_voice_narrates_when_the_step_names_no_voice()
    {
        VoiceoverFakes fakes = CreateFakes();
        List<SpeechRequest> requests = [];
        SetupSuccessfulSynthesis(fakes, requests);
        var resolver = new StubVoiceResolver();
        Guid lib = Guid.NewGuid();
        resolver.Voices[lib] = new ResolvedProjectVoice(lib, "Energetic Male", null, ProjectVoiceSource.Library, Guid.NewGuid(), "lib-voice", true, null);

        StepExecutionResult result = await CreateExecutorWithVoices(fakes, resolver).ExecuteAsync(CreateContext(UnvoicedConfigJson()));

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        requests.Should().ContainSingle().Which.VoiceId.Should().Be("lib-voice", "the only selectable voice, never the provider default");
        JsonElement voice = ParseOutput(result).GetProperty("meta").GetProperty("voice");
        voice.GetProperty("projectVoiceId").GetGuid().Should().Be(lib);
        voice.GetProperty("source").GetString().Should().Be("project_voice");
        voice.GetProperty("defaulted").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task A_project_with_no_selectable_voice_is_unchanged_and_speaks_in_the_provider_default()
    {
        VoiceoverFakes fakes = CreateFakes();
        List<SpeechRequest> requests = [];
        SetupSuccessfulSynthesis(fakes, requests);
        var resolver = new StubVoiceResolver();
        Guid revoked = Guid.NewGuid();
        resolver.Voices[revoked] = Clone(revoked, "Old voice", "fish-old", usable: false);

        StepExecutionResult result = await CreateExecutorWithVoices(fakes, resolver).ExecuteAsync(CreateContext(UnvoicedConfigJson()));

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        requests.Should().ContainSingle().Which.VoiceId.Should().BeNull("no selectable voice: the provider default, exactly as before");
        JsonElement voice = ParseOutput(result).GetProperty("meta").GetProperty("voice");
        voice.GetProperty("source").GetString().Should().Be("provider_default");
        voice.TryGetProperty("defaulted", out _).Should().BeFalse();
    }

    [Fact]
    public async Task A_clone_and_a_library_voice_together_are_still_ambiguous()
    {
        VoiceoverFakes fakes = CreateFakes();
        List<SpeechRequest> requests = [];
        SetupSuccessfulSynthesis(fakes, requests);
        var resolver = new StubVoiceResolver();
        Guid clone = Guid.NewGuid(), lib = Guid.NewGuid();
        resolver.Voices[clone] = Clone(clone, "Anna", "fish-a");
        resolver.Voices[lib] = new ResolvedProjectVoice(lib, "Energetic Male", null, ProjectVoiceSource.Library, Guid.NewGuid(), "lib-voice", true, null);

        StepExecutionResult result = await CreateExecutorWithVoices(fakes, resolver).ExecuteAsync(CreateContext(UnvoicedConfigJson()));

        result.Status.Should().Be(StepStatus.Failed);
        result.ErrorDetails.Should().StartWith("VOICE_CHOICE_REQUIRED");
        result.ErrorDetails.Should().Contain("2 voices").And.Contain("Anna").And.Contain("Energetic Male");
        requests.Should().BeEmpty("the provider's stock voice must not quietly narrate while the project owns voices");
    }

    [Fact]
    public async Task Several_clones_with_voice_casting_let_the_gate_choose_instead_of_failing()
    {
        VoiceoverFakes fakes = CreateFakes();
        List<SpeechRequest> requests = [];
        SetupSuccessfulSynthesis(fakes, requests);
        var resolver = new StubVoiceResolver();
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        resolver.Voices[a] = Clone(a, "Anna", "fish-a");
        resolver.Voices[b] = Clone(b, "Bruno", "fish-b");
        var gate = new Mock<IDecisionGate>();
        gate.Setup(g => g.DecideAsync(It.IsAny<DecisionGateRequest>(), It.IsAny<System.Threading.CancellationToken>()))
            .ReturnsAsync(new DecisionGateOutcome(true, true,
                new DecisionResult("jev", new Dictionary<string, DecisionAnswer>
                {
                    ["voice"] = new("voice", DecisionKind.Choice, b.ToString("D"), null, null, 0.93, new Dictionary<string, double>())
                }, 10, 2),
                new Dictionary<string, (double Top, double Second)> { ["voice"] = (0.93, 0.07) }));
        string json = SerializeConfig(new VoiceoverStepConfig(
            Source: new VoiceoverSource(VoiceoverSourceKind.Inline, new[] { new VoiceoverLine("Hello there.", 0.0) }),
            ProviderId: Guid.NewGuid(), Model: "test-model", MaxBytes: 2_000_000, VoiceCasting: VoiceCastingMode.Decision));

        StepExecutionResult result = await CreateExecutorWithVoices(fakes, resolver, gate.Object).ExecuteAsync(CreateContext(json));

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        requests.Should().ContainSingle().Which.VoiceId.Should().Be("fish-b");
    }

    // ------------------------------------------------------------------ cache key

    [Fact]
    public async Task The_default_voice_is_part_of_the_cache_key_so_adding_or_revoking_a_clone_misses()
    {
        var resolver = new StubVoiceResolver();
        VoiceoverStepConfig config = VoiceoverStepConfig.Parse(UnvoicedConfigJson())!;

        string? none = (await ProjectVoiceDefault.ResolveAsync(resolver, ProjectId, config, default)).CacheKeyComponent();
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        resolver.Voices[a] = Clone(a, "Anna", "fish-a");
        string? one = (await ProjectVoiceDefault.ResolveAsync(resolver, ProjectId, config, default)).CacheKeyComponent();
        resolver.Voices[b] = Clone(b, "Bruno", "fish-b");
        string? two = (await ProjectVoiceDefault.ResolveAsync(resolver, ProjectId, config, default)).CacheKeyComponent();
        resolver.Voices[a] = Clone(a, "Anna", "fish-a", usable: false);
        string? afterRevoke = (await ProjectVoiceDefault.ResolveAsync(resolver, ProjectId, config, default)).CacheKeyComponent();

        none.Should().BeNull("a project without a selectable voice keeps every key it had");

        // A Library voice is a selectable voice too: it names the component when it is the only one.
        var libraryResolver = new StubVoiceResolver();
        Guid lib = Guid.NewGuid();
        libraryResolver.Voices[lib] = new ResolvedProjectVoice(lib, "Energetic Male", null, ProjectVoiceSource.Library, Guid.NewGuid(), "lib-voice", true, null);
        string? libraryOnly = (await ProjectVoiceDefault.ResolveAsync(libraryResolver, ProjectId, config, default)).CacheKeyComponent();
        libraryOnly.Should().StartWith("single:").And.Contain(lib.ToString("N"));

        new[] { one, two, afterRevoke }.Should().OnlyHaveUniqueItems().And.NotContain((string?)null);
        two.Should().StartWith("choose:");
        one.Should().StartWith("single:").And.Contain(a.ToString("N"));
        afterRevoke.Should().Contain(b.ToString("N")).And.NotContain(a.ToString("N"));

        // A step that names a voice never takes a component, whatever the project owns.
        (await ProjectVoiceDefault.ResolveAsync(resolver, ProjectId, VoiceoverStepConfig.Parse(OneLineConfigJson())!, default))
            .CacheKeyComponent().Should().BeNull();

        var builder = new StepCacheKeyBuilder();
        string configJson = UnvoicedConfigJson();
        StepCacheKeyInputs Inputs(string? component) => new()
        {
            ProjectId = ProjectId,
            StepType = StepType.Voiceover,
            AgentType = AgentType.VideoTransform,
            AgentDefinitionId = Guid.Parse("11111111-2222-3333-4444-555555555555"),
            AgentInputContextMode = AgentInputContextMode.PreviousStepOnly,
            MaxIterations = 1,
            ProjectFileFingerprint = "fp",
            VoiceoverConfigJson = configJson,
            VoiceoverDefaultVoice = component
        };
        string keyNone = builder.Build(Inputs(null));
        builder.Build(Inputs(one)).Should().NotBe(keyNone);
        builder.Build(Inputs(two)).Should().NotBe(builder.Build(Inputs(one)));
        builder.Build(Inputs(null)).Should().Be(keyNone, "the component is appended only when present");
    }

    // ------------------------------------------------------------------ stale provider warning

    [Fact]
    public void A_replayed_narration_loses_the_provider_health_warnings_and_keeps_the_others()
    {
        string recorded = VoiceoverStepExecutor.WithWarnings("""{"view":{"lines":[]},"meta":{"totalLines":1}}""",
        [
            new VoiceoverStepExecutor.StepOutputWarning(VoiceoverStepExecutor.VoiceProviderUnhealthyCode, "failed its last connection check"),
            new VoiceoverStepExecutor.StepOutputWarning(VoiceoverStepExecutor.VoiceProviderFallbackCode, "another service narrated"),
            new VoiceoverStepExecutor.StepOutputWarning(VoiceoverStepExecutor.VoiceLanguageUnlistedCode, "language not listed")
        ]);

        string? replayed = VoiceoverStepExecutor.WithoutProviderHealthWarnings(recorded);

        JsonElement warnings = JsonDocument.Parse(replayed!).RootElement.GetProperty("warnings");
        warnings.EnumerateArray().Select(w => w.GetProperty("code").GetString())
            .Should().Equal(VoiceoverStepExecutor.VoiceLanguageUnlistedCode);
    }

    [Fact]
    public void A_replayed_narration_with_only_health_warnings_has_no_warnings_property_and_other_outputs_are_untouched()
    {
        string recorded = VoiceoverStepExecutor.WithWarnings("""{"view":{"lines":[]},"meta":{"totalLines":1}}""",
            [new VoiceoverStepExecutor.StepOutputWarning(VoiceoverStepExecutor.VoiceProviderUnhealthyCode, "x")]);

        JsonDocument.Parse(VoiceoverStepExecutor.WithoutProviderHealthWarnings(recorded)!).RootElement
            .TryGetProperty("warnings", out _).Should().BeFalse();

        const string clean = """{"view":{"lines":[]},"meta":{"totalLines":1}}""";
        VoiceoverStepExecutor.WithoutProviderHealthWarnings(clean).Should().BeSameAs(clean);
        VoiceoverStepExecutor.WithoutProviderHealthWarnings("not json").Should().Be("not json");
        VoiceoverStepExecutor.WithoutProviderHealthWarnings(null).Should().BeNull();
    }
}
