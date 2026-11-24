using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AwesomeAssertions;
using ReelBolt.Shared.Briefs;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Briefs;
using ReelBolt.WorkflowEngine.Services.Voiceover;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Execution.StepExecutors;

/// <summary>The run brief's Voiceover overrides, end to end through the executor with a fake speech client.</summary>
public partial class VoiceoverStepExecutorTests
{
    private static RunBriefState Brief(StoredRunBrief brief) => new(brief);

    [Fact]
    public async Task An_italian_brief_reaches_the_speech_client_as_it_with_the_chosen_voice()
    {
        VoiceoverFakes fakes = CreateFakes();
        List<SpeechRequest> requests = [];
        SetupSuccessfulSynthesis(fakes, requests);
        var resolver = new StubVoiceResolver();
        Guid voiceId = Guid.NewGuid();
        resolver.Voices[voiceId] = new ResolvedProjectVoice(voiceId, "Luca", null, ProjectVoiceSource.Library, Guid.NewGuid(), "remote-luca", true, null);

        using IDisposable scope = RunBriefAmbient.Enter(Brief(new StoredRunBrief(
            Language: "it", Voice: new RunBriefVoice(RunBriefVoiceMode.Narrated, LibraryVoiceId: voiceId))));
        StepExecutionResult result = await CreateExecutorWithVoices(fakes, resolver).ExecuteAsync(CreateContext(OneLineConfigJson()));

        result.Status.Should().Be(StepStatus.Completed, result.ErrorDetails ?? result.Output);
        SpeechRequest request = requests.Should().ContainSingle().Subject;
        request.Language.Should().Be("it");
        request.VoiceId.Should().Be("remote-luca");
        ParseOutput(result).GetProperty("meta").GetProperty("language").GetString().Should().Be("it");
    }

    [Fact]
    public async Task A_step_with_its_own_language_keeps_it_over_the_brief()
    {
        VoiceoverFakes fakes = CreateFakes();
        List<SpeechRequest> requests = [];
        SetupSuccessfulSynthesis(fakes, requests);
        string json = SerializeConfig(new VoiceoverStepConfig(
            Source: new VoiceoverSource(VoiceoverSourceKind.Inline, new[] { new VoiceoverLine("Hi.", 0.0) }),
            ProviderId: Guid.NewGuid(), Model: "m", Language: "en"));

        using IDisposable scope = RunBriefAmbient.Enter(Brief(new StoredRunBrief(Language: "it")));
        await CreateExecutor(fakes.SpeechFactory, fakes.ProviderResolver, fakes.MediaProbe, fakes.Workspace).ExecuteAsync(CreateContext(json));

        requests.Single().Language.Should().Be("en");
    }

    [Fact]
    public async Task A_voice_the_project_does_not_own_fails_closed_and_never_falls_back()
    {
        VoiceoverFakes fakes = CreateFakes();
        List<SpeechRequest> requests = [];
        SetupSuccessfulSynthesis(fakes, requests);

        using IDisposable scope = RunBriefAmbient.Enter(Brief(new StoredRunBrief(
            Voice: new RunBriefVoice(RunBriefVoiceMode.Narrated, ProjectVoiceId: Guid.NewGuid()))));
        StepExecutionResult result = await CreateExecutorWithVoices(fakes, new StubVoiceResolver()).ExecuteAsync(CreateContext(OneLineConfigJson()));

        result.Status.Should().Be(StepStatus.Failed);
        result.ErrorDetails.Should().StartWith("VOICE_NOT_FOUND");
        requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Voice_mode_none_skips_the_step_with_a_plain_reason_and_never_fails()
    {
        VoiceoverFakes fakes = CreateFakes();
        List<SpeechRequest> requests = [];
        SetupSuccessfulSynthesis(fakes, requests);

        using IDisposable scope = RunBriefAmbient.Enter(Brief(new StoredRunBrief(Voice: new RunBriefVoice(RunBriefVoiceMode.None))));
        StepExecutionResult result = await CreateExecutor(fakes.SpeechFactory, fakes.ProviderResolver, fakes.MediaProbe, fakes.Workspace)
            .ExecuteAsync(CreateContext(OneLineConfigJson()));

        result.Status.Should().Be(StepStatus.Skipped);
        result.ErrorDetails.Should().Be(VoiceoverStepExecutor.SilentRunReason);
        ParseOutput(result).GetProperty("skipped").GetBoolean().Should().BeTrue();
        requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_language_the_provider_does_not_list_warns_and_a_listed_or_unknown_one_does_not()
    {
        foreach ((string language, bool warns) in new[] { ("sv", true), ("it", false) })
        {
            VoiceoverFakes fakes = CreateFakes();
            SetupSuccessfulSynthesis(fakes, []);
            using IDisposable scope = RunBriefAmbient.Enter(Brief(new StoredRunBrief(Language: language)));
            StepExecutionResult result = await CreateExecutor(fakes.SpeechFactory, fakes.ProviderResolver, fakes.MediaProbe, fakes.Workspace)
                .ExecuteAsync(CreateContext(OneLineConfigJson()));

            result.Status.Should().Be(StepStatus.Completed, result.ErrorDetails ?? result.Output);
            bool has = ParseOutput(result).TryGetProperty("warnings", out JsonElement w)
                && w.EnumerateArray().Any(x => x.GetProperty("code").GetString() == VoiceoverStepExecutor.VoiceLanguageUnlistedCode);
            has.Should().Be(warns, language);
        }
    }

    [Fact]
    public async Task A_run_without_a_brief_sends_no_language()
    {
        VoiceoverFakes fakes = CreateFakes();
        List<SpeechRequest> requests = [];
        SetupSuccessfulSynthesis(fakes, requests);

        await CreateExecutor(fakes.SpeechFactory, fakes.ProviderResolver, fakes.MediaProbe, fakes.Workspace)
            .ExecuteAsync(CreateContext(OneLineConfigJson()));

        requests.Single().Language.Should().BeNull();
    }
}
