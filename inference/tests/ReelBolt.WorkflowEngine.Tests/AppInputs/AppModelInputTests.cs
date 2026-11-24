using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Briefs;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Tenancy;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.Apps;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution.Caching;
using ReelBolt.WorkflowEngine.Execution.Snapshots;
using Xunit;
using static ReelBolt.WorkflowEngine.Tests.AppInputs.AppInputsTestKit;

namespace ReelBolt.WorkflowEngine.Tests.AppInputs;

/// <summary>
/// Lane L4 (docs/design/app-inputs.md 8): a <c>model</c> input binds a chosen, selectable, allowed model and nothing else;
/// the agent model rides the run's agent snapshot; the chosen model moves the step-cache key through the resolved-provider
/// fingerprint and the cold-start price through the shared profile pricing.
/// </summary>
public class AppModelInputTests
{
    private static readonly Guid Cheap = Guid.Parse("11111111-0000-0000-0000-000000000001");
    private static readonly Guid Dear = Guid.Parse("11111111-0000-0000-0000-000000000002");
    private static readonly Guid Unlisted = Guid.Parse("11111111-0000-0000-0000-000000000003");
    private static readonly Guid ChatCheap = Guid.Parse("22222222-0000-0000-0000-000000000001");
    private static readonly Guid ChatDear = Guid.Parse("22222222-0000-0000-0000-000000000002");
    private static readonly Guid Writer = Guid.Parse("33333333-0000-0000-0000-000000000001");

    private static readonly IReadOnlyList<AppModelCandidate> Candidates =
    [
        new(Cheap, "VideoGeneration", "MiniMax", "MiniMax-H3", "Hailuo", IsPlatform: true, IsDefault: true),
        new(Dear, "VideoGeneration", "MiniMax", "MiniMax-H3-Pro", "Hailuo Pro", IsPlatform: true),
        new(Unlisted, "VideoGeneration", "MiniMax", "MiniMax-H2", "Hailuo 2", IsPlatform: true),
        new(ChatCheap, "Chat", "DeepSeek", "cheap-m", "Cheap chat", IsPlatform: true, IsDefault: true),
        new(ChatDear, "Chat", "DeepSeek", "pricier-m", "Pricier chat", IsPlatform: true)
    ];

    private static readonly AppManifest VideoApp = Manifest(
        Input("videoModel", "model", "Video model", capability: "VideoGeneration", allowed: [Cheap, Dear]));

    private static readonly AppManifest AgentApp = Manifest(
        Input("scriptModel", "model", "Script model", capability: "Chat", agent: Writer));

    private const string Generate =
        """{"planSource":"Inline","inlinePrompt":"A barista","durationSeconds":6,"takes":1,"maxClips":1,"maxSpendCredits":5,"appInputs":{"@model":"videoModel"}}""";

    private static WorkflowStep VideoStep(int order = 1, string config = Generate) => new()
    {
        Id = Guid.NewGuid(),
        StepOrder = order,
        StepType = StepType.VideoGenerate,
        AgentDefinitionId = Guid.NewGuid(),
        VideoGenerateConfigJson = config
    };

    private static WorkflowStep AgentStep(int order = 1) => new()
    {
        Id = Guid.NewGuid(),
        StepOrder = order,
        StepType = StepType.Agent,
        AgentDefinitionId = Writer
    };

    // ---------------- the run gate (API) ----------------

    [Fact]
    public void A_selectable_allowed_model_binds_its_provider_and_model_code()
    {
        AppInputGateResult gate = AppInputValueValidator.Evaluate(VideoApp, [VideoStep()], Values(("videoModel", $"\"{Dear}\"")), null,
            candidates: Candidates);

        gate.Refused.Should().BeFalse(gate.Message);
        string bound = gate.BoundSteps![0].VideoGenerateConfigJson!;
        bound.Should().Contain($"\"providerId\":\"{Dear:D}\"").And.Contain("\"model\":\"MiniMax-H3-Pro\"").And.NotContain("appInputs");
    }

    [Fact]
    public void A_model_the_organization_cannot_select_is_refused_before_anything_is_charged()
    {
        // Allowed by the author, but not among the run organization's selectable models (another organization's row, or
        // disabled, or unpriced): the gate refuses it with the model code.
        AppManifest app = Manifest(Input("videoModel", "model", "Video model", capability: "VideoGeneration"));
        Guid foreign = Guid.NewGuid();

        AppInputGateResult gate = AppInputValueValidator.Evaluate(app, [VideoStep()], Values(("videoModel", $"\"{foreign}\"")), null,
            candidates: Candidates);

        gate.Code.Should().Be(AppInputValueValidator.ModelUnavailableCode);
        gate.InputId.Should().Be("videoModel");
        gate.BoundSteps.Should().BeNull();
        System.Text.Json.JsonSerializer.Serialize(gate.ErrorBody()).Should().Contain("\"inputId\":\"videoModel\"");
    }

    [Fact]
    public void A_model_outside_the_authors_list_is_refused_with_the_model_code()
    {
        AppInputGateResult gate = AppInputValueValidator.Evaluate(VideoApp, [VideoStep()], Values(("videoModel", $"\"{Unlisted}\"")), null,
            candidates: Candidates);

        gate.Code.Should().Be(AppInputValueValidator.ModelUnavailableCode);
        gate.Message.Should().Contain("Video model");
    }

    [Fact]
    public void A_model_of_another_capability_is_refused()
    {
        AppManifest app = Manifest(Input("videoModel", "model", "Video model", capability: "VideoGeneration"));

        AppInputGateResult gate = AppInputValueValidator.Evaluate(app, [VideoStep()], Values(("videoModel", $"\"{ChatDear}\"")), null,
            candidates: Candidates);

        gate.Code.Should().Be(AppInputValueValidator.ModelUnavailableCode);
    }

    [Fact]
    public void An_agent_model_input_is_resolved_and_returned_for_pricing_and_a_bad_one_is_refused()
    {
        AppInputGateResult ok = AppInputValueValidator.Evaluate(AgentApp, [AgentStep()], Values(("scriptModel", $"\"{ChatDear}\"")), null,
            candidates: Candidates);
        ok.Refused.Should().BeFalse(ok.Message);
        ok.AgentModels.Should().ContainKey(Writer).WhoseValue.Should().Be(ChatDear);

        AppInputGateResult video = AppInputValueValidator.Evaluate(AgentApp, [AgentStep()], Values(("scriptModel", $"\"{Dear}\"")), null,
            candidates: Candidates);
        video.Code.Should().Be(AppInputValueValidator.ModelUnavailableCode, "a video model cannot be an agent's chat model");

        AppInputGateResult unregistered = AppInputValueValidator.Evaluate(AgentApp, [AgentStep()], Values(("scriptModel", $"\"{ChatDear}\"")), null);
        unregistered.Code.Should().Be(AppInputValueValidator.ModelUnavailableCode, "with no model source every model value is refused");

        AppInputGateResult none = AppInputValueValidator.Evaluate(AgentApp, [AgentStep()], null, null, candidates: Candidates);
        none.Refused.Should().BeFalse();
        none.AgentModels.Should().BeNull("no value chosen: the agent runs on its ordinary model");
    }

    // ---------------- the pure rules ----------------

    [Fact]
    public void The_job_comes_from_the_steps_own_config_and_a_talking_head_needs_a_talking_head_model()
    {
        AppBindingStep text = Step(1, StepType.VideoGenerate, """{"planSource":"Inline","inlinePrompt":"x"}""");
        AppBindingStep talking = Step(1, StepType.VideoGenerate,
            $$"""{"planSource":"Inline","avatarId":"{{Guid.NewGuid()}}","avatarAudioProjectFileId":"{{Guid.NewGuid()}}"}""");
        AppBindingStep reference = Step(1, StepType.VideoGenerate, $$"""{"planSource":"Inline","inlinePrompt":"x","avatarId":"{{Guid.NewGuid()}}"}""");

        AppModelRules.JobFor(text).Should().Be(AppModelRules.TextToVideo);
        AppModelRules.JobFor(talking).Should().Be(AppModelRules.TalkingHead);
        AppModelRules.JobFor(reference).Should().Be(AppModelRules.Reference);

        AppModelCandidate minimax = Candidates[0];
        AppModelCandidate lipSync = new(Guid.NewGuid(), "LipSync", "FalAi", "minimax/h3-max/lip-sync/image-to-video", "Lip sync", true);
        AppModelCandidate staleRow = new(Guid.NewGuid(), "VideoGeneration", "FalAi", "minimax/h3-max/lip-sync/image-to-video", "Old lip sync", true);
        AppModelRules.ServesJob(minimax, AppModelRules.TalkingHead).Should().BeFalse();
        AppModelRules.ServesJob(staleRow, AppModelRules.TalkingHead).Should().BeFalse("a VideoGeneration row no longer serves a talking head");
        AppModelRules.ServesJob(lipSync, AppModelRules.TalkingHead).Should().BeTrue();
        AppModelRules.ServesJob(lipSync, AppModelRules.TextToVideo).Should().BeFalse("a talking-head endpoint makes nothing else");

        // A talking head chooses from LipSync models; any other VideoGenerate step stays on VideoGeneration.
        AppModelRules.CapabilityFor(talking).Should().Be("LipSync");
        AppModelRules.CapabilityFor(text).Should().Be("VideoGeneration");
        AppModelRules.CapabilityFor(reference).Should().Be("VideoGeneration");

        AppManifest app = Manifest(Input("videoModel", "model", "Lip-sync model", capability: "LipSync"));
        AppModelResolver resolve = AppModelRules.Resolver(app, [.. Candidates, lipSync, staleRow]);
        resolve("videoModel", minimax.Id, talking).Should().BeNull();
        resolve("videoModel", staleRow.Id, talking).Should().BeNull();
        resolve("videoModel", lipSync.Id, talking).Should().Be(new AppModelTarget(lipSync.Id, lipSync.ModelCode));
        resolve("videoModel", lipSync.Id, text).Should().BeNull("a LipSync model does not serve a text-to-video step");

        // A manifest saved before the capability existed names VideoGeneration for its talking-head step: still read as LipSync.
        AppManifest saved = Manifest(Input("videoModel", "model", "Video model", capability: "VideoGeneration"));
        AppModelRules.Resolver(saved, [lipSync])("videoModel", lipSync.Id, talking).Should().Be(new AppModelTarget(lipSync.Id, lipSync.ModelCode));
    }

    [Fact]
    public void The_plan_gate_treats_a_lip_sync_model_like_a_video_model_that_shows_an_avatar()
    {
        OrganizationEntitlementSet noAvatars = OrganizationEntitlementSet.Unlimited with { AvatarsAllowed = false };
        AppModelRules.PlanFeature("LipSync", AppModelRules.TalkingHead, noAvatars).Should().Be(PlanGate.AvatarsFeature);
        AppModelRules.PlanFeature("LipSync", AppModelRules.TalkingHead, noAvatars with { AvatarsAllowed = true, VideoGenAllowed = false })
            .Should().Be(PlanGate.VideoGenerationFeature);
        AppModelRules.PlanFeature("LipSync", AppModelRules.TalkingHead, OrganizationEntitlementSet.Unlimited with { MaxOutputHeight = 720 }, clipShortSide: 768)
            .Should().Be(PlanGate.ResolutionFeature);
    }

    [Fact]
    public void An_audio_step_takes_the_capability_of_its_kind()
    {
        AppModelRules.CapabilityFor(Step(1, StepType.AudioGenerate, """{"kind":"Music","prompt":"x","durationSec":30}""")).Should().Be("MusicGeneration");
        AppModelRules.CapabilityFor(Step(1, StepType.AudioGenerate, """{"kind":"SoundEffect","prompt":"x","durationSec":3}""")).Should().Be("SoundEffectGeneration");
        AppModelRules.CapabilityFor(Step(1, StepType.Voiceover, "{}")).Should().Be("SpeechSynthesis");
        AppModelRules.CapabilityFor(Step(1, StepType.VideoCompile, "{}")).Should().BeNull();
    }

    // ---------------- the engine's snapshot ----------------

    private static WorkflowEngineDbContext Db() =>
        new(new DbContextOptionsBuilder<WorkflowEngineDbContext>().UseInMemoryDatabase("l4-" + Guid.NewGuid().ToString("N")).Options);

    private static void SeedModel(WorkflowEngineDbContext db, Guid capabilityId, InferenceProviderCapability capability, string code, bool enabled = true)
    {
        InferenceProvider provider = new() { Id = Guid.NewGuid(), Name = "P" + code, Kind = InferenceProviderKind.MiniMax, Endpoint = "https://p.example" };
        InferenceModel model = new() { Id = Guid.NewGuid(), ProviderId = provider.Id, ModelCode = code, DisplayName = code, Provider = provider };
        db.ModelCapabilities.Add(new ModelCapability { Id = capabilityId, ModelId = model.Id, Capability = capability, IsEnabled = enabled, Model = model });
    }

    private static async Task<WorkflowExecution> SeedRunAsync(WorkflowEngineDbContext db, AppManifest manifest, string briefJson, params WorkflowStep[] steps)
    {
        WorkflowDefinition workflow = new()
        {
            Id = Guid.NewGuid(),
            Name = "Model app",
            ProjectId = Guid.NewGuid(),
            Kind = WorkflowDefinitionKind.Standard,
            AppManifestJson = AppManifestJson.Serialize(manifest),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        foreach (WorkflowStep step in steps)
        {
            step.WorkflowDefinitionId = workflow.Id;
            workflow.Steps.Add(step);
        }

        WorkflowExecution execution = new()
        {
            Id = Guid.NewGuid(),
            ProjectId = workflow.ProjectId,
            OrganizationId = Guid.NewGuid(),
            WorkflowDefinitionId = workflow.Id,
            RunBriefJson = briefJson,
            Status = ExecutionStatus.Running
        };
        db.AgentDefinitions.Add(new AgentDefinition { Id = Writer, Name = "Writer", AgentType = AgentType.ScriptwriterAgent, IsBuiltIn = true, SystemPrompt = "x" });
        db.WorkflowDefinitions.Add(workflow);
        db.WorkflowExecutions.Add(execution);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return await db.WorkflowExecutions.Include(e => e.WorkflowDefinition).ThenInclude(w => w.Steps).SingleAsync(e => e.Id == execution.Id);
    }

    private static string Brief(params (string Id, string Raw)[] values) =>
        RunBriefJson.Serialize(new StoredRunBrief(Inputs: Values(values)));

    [Fact]
    public async Task The_snapshot_binds_the_chosen_model_re_resolved_for_the_run()
    {
        await using WorkflowEngineDbContext db = Db();
        SeedModel(db, Cheap, InferenceProviderCapability.VideoGeneration, "MiniMax-H3");
        SeedModel(db, Dear, InferenceProviderCapability.VideoGeneration, "MiniMax-H3-Pro");
        WorkflowExecution execution = await SeedRunAsync(db, VideoApp, Brief(("videoModel", $"\"{Dear}\"")), VideoStep());

        ExecutionRunPlan plan = await ExecutionSnapshotter.PrepareAsync(db, execution, CancellationToken.None);

        string config = plan.Steps.Single().VideoGenerateConfigJson!;
        config.Should().Contain($"\"providerId\":\"{Dear:D}\"").And.Contain("\"model\":\"MiniMax-H3-Pro\"");
        plan.AppInputs!.ComponentFor(1).Should().Contain(Dear.ToString("D"), "the chosen model is in the step's cache component");
    }

    [Fact]
    public async Task A_model_disabled_between_Run_and_the_claim_fails_the_run_as_stale()
    {
        await using WorkflowEngineDbContext db = Db();
        SeedModel(db, Dear, InferenceProviderCapability.VideoGeneration, "MiniMax-H3-Pro", enabled: false);
        WorkflowExecution execution = await SeedRunAsync(db, VideoApp, Brief(("videoModel", $"\"{Dear}\"")), VideoStep());

        Func<Task> take = () => ExecutionSnapshotter.PrepareAsync(db, execution, CancellationToken.None);

        (await take.Should().ThrowAsync<AppInputsStaleException>()).Which.Code.Should().Be("APP_INPUTS_STALE");
    }

    [Fact]
    public async Task An_agent_model_choice_rides_the_agent_snapshot_and_no_choice_writes_nothing_new()
    {
        await using WorkflowEngineDbContext db = Db();
        SeedModel(db, ChatDear, InferenceProviderCapability.Chat, "pricier-m");
        WorkflowExecution chosen = await SeedRunAsync(db, AgentApp, Brief(("scriptModel", $"\"{ChatDear}\"")), AgentStep());

        ExecutionRunPlan plan = await ExecutionSnapshotter.PrepareAsync(db, chosen, CancellationToken.None);

        plan.Agents!.Find(Writer)!.AppModelProviderId.Should().Be(ChatDear);
        plan.Agents.AppModels().Should().ContainKey(Writer);
        AgentSnapshot.Parse(plan.Agents.Serialize())!.Find(Writer)!.AppModelProviderId.Should().Be(ChatDear);

        AgentSnapshot plain = AgentSnapshot.Capture([new AgentDefinition { Id = Writer, Name = "Writer", SystemPrompt = "x" }]);
        plain.Serialize().Should().NotContain("appModelProviderId", "a run without a choice serializes as before");
        plain.WithAppModels(null).Should().BeSameAs(plain);
        plain.AppModels().Should().BeNull();
    }

    // ---------------- the options endpoint ----------------

    private sealed class FixedCandidates(IReadOnlyList<AppModelCandidate> candidates) : IAppInputModelResolverSource
    {
        public Task<IReadOnlyList<AppModelCandidate>> CandidatesAsync(Guid? organizationId, Guid projectId, CancellationToken ct) =>
            Task.FromResult(candidates);
    }

    [Fact]
    public async Task The_options_are_the_authors_list_recommended_first_marked_as_the_run_gate_would_judge_them()
    {
        await using ReelBolt.Inference.Api.Data.InferenceApiDbContext db =
            new(new DbContextOptionsBuilder<ReelBolt.Inference.Api.Data.InferenceApiDbContext>().UseInMemoryDatabase("l4-api-" + Guid.NewGuid().ToString("N")).Options);
        AppModelCandidate lipSync = new(Guid.NewGuid(), "VideoGeneration", "FalAi", "minimax/h3-max/lip-sync/image-to-video", "Lip sync", true);
        AppManifest app = Manifest(Input("videoModel", "model", "Video model", capability: "VideoGeneration", allowed: [Cheap, Dear, lipSync.Id])) is var m
            ? m with { Inputs = [m.Inputs![0] with { Recommended = Dear }] }
            : null!;
        Guid projectId = Guid.NewGuid();
        WorkflowDefinition workflow = new()
        {
            Id = Guid.NewGuid(),
            Name = "Clip",
            ProjectId = projectId,
            Kind = WorkflowDefinitionKind.Standard,
            AppManifestJson = AppManifestJson.Serialize(app),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        WorkflowStep step = VideoStep();
        step.WorkflowDefinitionId = workflow.Id;
        workflow.Steps.Add(step);
        db.WorkflowDefinitions.Add(workflow);
        await db.SaveChangesAsync();

        AppInputModelOptionsService service = new(db, new FixedCandidates([.. Candidates, lipSync]));
        AppInputModelOptionsResponse? options = await service.OptionsAsync(null, projectId, workflow.Id, "videoModel", CancellationToken.None);

        options.Should().NotBeNull();
        options!.Capability.Should().Be("VideoGeneration");
        options.Job.Should().Be(AppModelRules.TextToVideo);
        options.Options.Select(o => o.Id).Should().BeEquivalentTo([Cheap, Dear, lipSync.Id], "only the author's list, never the unlisted model");
        options.Options[0].Id.Should().Be(Dear);
        options.Options[0].Recommended.Should().BeTrue();
        options.Options.Single(o => o.Id == lipSync.Id).Available.Should().BeFalse("a talking-head endpoint cannot make this clip");
        options.Options.Single(o => o.Id == lipSync.Id).UnavailableCode.Should().Be(AppModelRules.WrongJob, "the page words the reason from a stable code");
        options.Options.Single(o => o.Id == Cheap).UnavailableCode.Should().BeNull();
        options.Options.Single(o => o.Id == Cheap).Available.Should().BeTrue();

        // The builder's "Allowed models" editor (docs/design/app-inputs.md 24) sees every model of the kind, the list ignored.
        AppModelCandidate other = new(Guid.NewGuid(), "VideoGeneration", "MiniMax", "MiniMax-H4", "Hailuo 4", true);
        AppInputModelOptionsService wide = new(db, new FixedCandidates([.. Candidates, lipSync, other]));
        AppInputModelOptionsResponse? all = await wide.OptionsAsync(null, projectId, workflow.Id, "videoModel", CancellationToken.None, ignoreAllowed: true);
        all!.Options.Select(o => o.Id).Should().BeEquivalentTo([.. Candidates.Where(c => c.Capability == "VideoGeneration").Select(c => c.Id), lipSync.Id, other.Id], "the author may widen the list");
        all.Options.Single(o => o.Id == other.Id).Available.Should().BeTrue("outside the list is not a refusal in the builder's view");
        all.Options.Single(o => o.Id == Dear).Recommended.Should().BeTrue("the author's recommendation still stands");
        all.Options.Single(o => o.Id == lipSync.Id).UnavailableCode.Should().Be(AppModelRules.WrongJob);
        (await wide.OptionsAsync(null, projectId, workflow.Id, "videoModel", CancellationToken.None))!.Options.Select(o => o.Id)
            .Should().NotContain(other.Id, "the run page still offers only the author's list");

        (await service.OptionsAsync(null, projectId, workflow.Id, "nope", CancellationToken.None)).Should().BeNull();
        (await service.OptionsAsync(null, Guid.NewGuid(), workflow.Id, "videoModel", CancellationToken.None)).Should().BeNull("another project's workflow is not found");
    }

    [Fact]
    public async Task A_picture_model_option_carries_a_relative_price_like_a_chat_model()
    {
        await using ReelBolt.Inference.Api.Data.InferenceApiDbContext db =
            new(new DbContextOptionsBuilder<ReelBolt.Inference.Api.Data.InferenceApiDbContext>().UseInMemoryDatabase("vision-options-" + Guid.NewGuid().ToString("N")).Options);
        AppModelCandidate cheap = new(Guid.NewGuid(), "Vision", "DeepSeek", "cheap-m", "Cheap eyes", IsPlatform: true, IsDefault: true);
        AppModelCandidate dear = new(Guid.NewGuid(), "Vision", "DeepSeek", "pricier-m", "Sharper eyes", IsPlatform: true);
        AppModelCandidate own = new(Guid.NewGuid(), "Vision", "OpenAICompatible", "cheap-m", "Our own", IsPlatform: false);
        Guid projectId = Guid.NewGuid();
        WorkflowDefinition workflow = new()
        {
            Id = Guid.NewGuid(),
            Name = "Sounds",
            ProjectId = projectId,
            Kind = WorkflowDefinitionKind.Standard,
            AppManifestJson = AppManifestJson.Serialize(Manifest(Input("lookModel", "model", "Model that looks at the picture", capability: "Vision"))),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        workflow.Steps.Add(new WorkflowStep
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = workflow.Id,
            StepOrder = 1,
            StepType = StepType.VideoAnalyze,
            VideoAnalyzeConfigJson = """{"version":1,"source":{"kind":"ProjectFile"},"analyzeEvents":true,"describeEvents":true,"appInputs":{"@visionModel":"lookModel"}}"""
        });
        db.WorkflowDefinitions.Add(workflow);
        await db.SaveChangesAsync();

        AppInputModelOptionsService service = new(db, new FixedCandidates([.. Candidates, cheap, dear, own]), prices: new ByModelPrices());
        AppInputModelOptionsResponse? options = await service.OptionsAsync(null, projectId, workflow.Id, "lookModel", CancellationToken.None);

        options!.Capability.Should().Be("Vision");
        options.Options.Select(o => o.Id).Should().BeEquivalentTo([cheap.Id, dear.Id, own.Id]);
        options.Options.Single(o => o.Id == cheap.Id).CostIndex.Should().Be(1m, "the recommended model is the base of the ratio");
        options.Options.Single(o => o.Id == dear.Id).CostIndex.Should().Be(7m, "priced per token on its Vision prices");
        options.Options.Single(o => o.Id == own.Id).CostIndex.Should().BeNull("an organization's own provider has no platform price");
    }

    // ---------------- cache key and price ----------------

    [Fact]
    public async Task The_chosen_agent_model_changes_the_resolved_provider_fingerprint_and_no_choice_changes_nothing()
    {
        InferenceProviderResolver resolver = Resolver(Guid.NewGuid(), builtIn: new() { [AgentType.ScriptwriterAgent] = ChatCheap });

        string legacy = await ResolvedProviderFingerprint.ComputeAsync(resolver, AgentType.ScriptwriterAgent, Writer, [], CancellationToken.None);
        string none = await ResolvedProviderFingerprint.ComputeAsync(resolver, AgentType.ScriptwriterAgent, Writer, null, [], CancellationToken.None);
        string dear = await ResolvedProviderFingerprint.ComputeAsync(resolver, AgentType.ScriptwriterAgent, Writer, ChatDear, [], CancellationToken.None);
        string same = await ResolvedProviderFingerprint.ComputeAsync(resolver, AgentType.ScriptwriterAgent, Writer, ChatCheap, [], CancellationToken.None);
        string gone = await ResolvedProviderFingerprint.ComputeAsync(resolver, AgentType.ScriptwriterAgent, Writer, Guid.NewGuid(), [], CancellationToken.None);

        none.Should().Be(legacy);
        dear.Should().NotBe(legacy, "a different chat model is different work");
        same.Should().Be(legacy, "choosing the model the agent would use anyway is the same work");
        gone.Should().Be(legacy, "a choice that no longer resolves runs on the ordinary model, and keys as such");
    }

    [Fact]
    public async Task The_cold_start_price_of_an_agent_follows_its_chosen_model()
    {
        InferenceProviderResolver resolver = Resolver(Guid.NewGuid(), builtIn: new() { [AgentType.ScriptwriterAgent] = ChatCheap });
        ProfileQuantity input = new(UsageKind.LlmInputTokens, ProfilePriceTarget.Chat, 1_000m);

        async Task<long> QuoteAsync()
        {
            IProfileQuantityPricer pricer = await ProfilePriceTable.BuildAsync(
                [ProfilePriceKey.Of(input, AgentType.ScriptwriterAgent, Writer)], resolver, new ByModelPrices(), NullLogger.Instance, CancellationToken.None);
            return pricer.Price(input, AgentType.ScriptwriterAgent, Writer).CreditsMillis;
        }

        long ordinary = await QuoteAsync();
        long chosen;
        using (AppAgentModelAmbient.Enter(new Dictionary<Guid, Guid> { [Writer] = ChatDear }))
            chosen = await QuoteAsync();
        long other;
        using (AppAgentModelAmbient.Enter(new Dictionary<Guid, Guid> { [Guid.NewGuid()] = ChatDear }))
            other = await QuoteAsync();

        ordinary.Should().Be(1_000);
        chosen.Should().Be(7_000, "a dearer chosen model raises the hold and the estimate");
        other.Should().Be(ordinary, "a choice for another agent moves nothing");
    }

    // ---------------- follow-up F2 (docs/design/app-inputs.md 28): baselines and the chosen speech model ----------------

    [Fact]
    public async Task A_baseline_key_prices_the_agents_ordinary_model_while_the_plain_key_prices_the_chosen_one()
    {
        InferenceProviderResolver resolver = Resolver(Guid.NewGuid(), builtIn: new() { [AgentType.ScriptwriterAgent] = ChatCheap });
        ProfileQuantity input = new(UsageKind.LlmInputTokens, ProfilePriceTarget.Chat, 1_000m);
        ProfilePriceKey plain = ProfilePriceKey.Of(input, AgentType.ScriptwriterAgent, Writer);
        ProfilePriceKey baseline = ProfilePriceKey.Of(input, AgentType.ScriptwriterAgent, Writer, baseline: true);
        baseline.Should().NotBe(plain);
        ProfilePriceKey.Of(new ProfileQuantity(UsageKind.AnalyzeMediaSeconds, ProfilePriceTarget.Compute, 1m), AgentType.Custom, null, baseline: true)
            .Baseline.Should().BeFalse("only a chat quantity has an ordinary model of its own");

        using (AppAgentModelAmbient.Enter(new Dictionary<Guid, Guid> { [Writer] = ChatDear }))
        {
            IProfileQuantityPricer pricer = await ProfilePriceTable.BuildAsync(
                [plain, baseline], resolver, new ByModelPrices(), NullLogger.Instance, CancellationToken.None);
            pricer.Price(input, AgentType.ScriptwriterAgent, Writer).CreditsMillis.Should().Be(7_000);
            pricer.PriceBaseline(input, AgentType.ScriptwriterAgent, Writer).CreditsMillis.Should().Be(1_000, "the model the agent's history ran on");
        }
    }

    private static readonly Guid VoiceDefault = Guid.Parse("44444444-0000-0000-0000-000000000001");
    private static readonly Guid VoiceChosen = Guid.Parse("44444444-0000-0000-0000-000000000002");

    private static WorkflowStep VoiceoverStep(Guid? providerId) => new()
    {
        Id = Guid.NewGuid(),
        StepOrder = 1,
        StepType = StepType.Voiceover,
        AgentDefinitionId = Guid.NewGuid(),
        VoiceoverConfigJson = providerId is { } id
            ? $$"""{"source":{"kind":"Inline","lines":[{"text":"Hello","startSec":0}]},"providerId":"{{id}}","narrationFitPolicy":"Report"}"""
            : """{"source":{"kind":"Inline","lines":[{"text":"Hello","startSec":0}]},"narrationFitPolicy":"Report"}"""
    };

    [Fact]
    public void A_voiceover_prices_its_speech_at_the_model_the_app_chose_and_nowhere_else()
    {
        static Guid?[] SpeechProviders(WorkflowStep step) =>
            [.. StepUsageProfiles.For(step, null).Quantities.Where(q => q.Target == ProfilePriceTarget.SpeechSynthesis).Select(q => q.ProviderId)];

        SpeechProviders(VoiceoverStep(VoiceChosen)).Should().Equal([null], "no choice: the default speech model, as before");
        using (AppAgentModelAmbient.Enter(null, new HashSet<Guid> { VoiceChosen }))
        {
            SpeechProviders(VoiceoverStep(VoiceChosen)).Should().Equal([VoiceChosen], "the bound provider is the app's choice");
            SpeechProviders(VoiceoverStep(VoiceDefault)).Should().Equal([null], "an author-set provider the app did not choose keeps today's price");
            SpeechProviders(VoiceoverStep(null)).Should().Equal([null]);
            ProfilePriceKey.Of(new ProfileQuantity(UsageKind.TtsInputBytes, ProfilePriceTarget.SpeechSynthesis, 1m, VoiceChosen), AgentType.Custom, null)
                .ProviderId.Should().Be(VoiceChosen);
        }
    }

    [Fact]
    public async Task The_chosen_speech_model_is_the_one_the_table_prices()
    {
        ServiceCollection services = new();
        services.AddSingleton<IInferenceProviderStore>(new Store(
            [SpeechRow(VoiceDefault, "cheap-m", isDefault: true), SpeechRow(VoiceChosen, "pricier-m", isDefault: false)], new()));
        ServiceProvider sp = services.BuildServiceProvider();
        InferenceProviderResolver resolver = new(
            sp.GetRequiredService<IServiceScopeFactory>(),
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build(),
            new Passthrough(), NullLogger<InferenceProviderResolver>.Instance, new Tenant(Guid.NewGuid()));
        ProfileQuantity byDefault = new(UsageKind.TtsInputBytes, ProfilePriceTarget.SpeechSynthesis, 1_000m);
        ProfileQuantity chosen = byDefault with { ProviderId = VoiceChosen };

        IProfileQuantityPricer pricer = await ProfilePriceTable.BuildAsync(
            [ProfilePriceKey.Of(byDefault, AgentType.Custom, null), ProfilePriceKey.Of(chosen, AgentType.Custom, null)],
            resolver, new ByModelPrices(), NullLogger.Instance, CancellationToken.None);

        pricer.Price(byDefault, AgentType.Custom, null).CreditsMillis.Should().Be(1_000);
        pricer.Price(chosen, AgentType.Custom, null).CreditsMillis.Should().Be(7_000, "the chosen speech model is priced, not the default");
    }

    private static ModelCapabilityView SpeechRow(Guid id, string code, bool isDefault) => new()
    {
        Id = id,
        Name = code,
        Kind = InferenceProviderKind.FishAudio,
        Capability = InferenceProviderCapability.SpeechSynthesis,
        Endpoint = "https://provider.example",
        ModelName = code,
        IsDefault = isDefault,
        IsEnabled = true,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    private static InferenceProviderResolver Resolver(Guid org, Dictionary<AgentType, Guid?> builtIn)
    {
        ServiceCollection services = new();
        services.AddSingleton<IInferenceProviderStore>(new Store([ChatRow(ChatCheap, "cheap-m"), ChatRow(ChatDear, "pricier-m")], builtIn));
        ServiceProvider sp = services.BuildServiceProvider();
        return new InferenceProviderResolver(
            sp.GetRequiredService<IServiceScopeFactory>(),
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build(),
            new Passthrough(),
            NullLogger<InferenceProviderResolver>.Instance,
            new Tenant(org));
    }

    private static ModelCapabilityView ChatRow(Guid id, string code) => new()
    {
        Id = id,
        Name = code,
        Kind = InferenceProviderKind.DeepSeek,
        Capability = InferenceProviderCapability.Chat,
        Endpoint = "https://provider.example",
        ModelName = code,
        IsEnabled = true,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    private sealed class Tenant(Guid org) : InferenceTenantAccessorBase
    {
        protected override Guid? AmbientOrganizationId => org;
    }

    private sealed class Passthrough : ISecretProtector
    {
        public string Protect(string plaintext) => plaintext;

        public bool TryUnprotect(string? ciphertext, out string plaintext)
        {
            plaintext = ciphertext ?? string.Empty;
            return ciphertext != null;
        }
    }

    private sealed class Store(IReadOnlyList<ModelCapabilityView> rows, Dictionary<AgentType, Guid?> builtIn) : IInferenceProviderStore
    {
        public Task<IReadOnlyList<ModelCapabilityView>> LoadEnabledAsync(Guid? organizationId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ModelCapabilityView>>(rows.ToList());

        public Task<IReadOnlyDictionary<Guid, Guid?>> LoadAgentOverridesAsync(Guid? organizationId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<Guid, Guid?>>(new Dictionary<Guid, Guid?>());

        public Task<IReadOnlyDictionary<AgentType, Guid?>> LoadOrgAgentOverridesAsync(Guid? organizationId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<AgentType, Guid?>>(new Dictionary<AgentType, Guid?>());

        public Task<IReadOnlyDictionary<AgentType, Guid?>> LoadBuiltInAgentOverridesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<AgentType, Guid?>>(builtIn);

        public Task<IReadOnlySet<Guid>> LoadUnpricedCapabilityIdsAsync(DateTime at, CancellationToken ct) =>
            Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>());
    }

    /// <summary>Charges per token by model code, so a quote moves only when the model moved.</summary>
    private sealed class ByModelPrices : IModelPriceService
    {
        public ValueTask<PriceQuote> PriceAsync(PriceRequest request, CancellationToken ct = default)
        {
            long perToken = request.ModelCode == "cheap-m" ? 1 : request.ModelCode == "pricier-m" ? 7
                : throw new UnpricedUsageException(request.Unit, request.Kind, request.ModelCode ?? string.Empty);
            return ValueTask.FromResult(new PriceQuote((long)(request.Quantity * perToken), 0m, null));
        }

        public ValueTask<PriceQuote> PriceQuotedUsdAsync(
            decimal costUsd, UsageKind unit, UsageBillingMode billingMode, DateTime? at = null, CancellationToken ct = default) =>
            ValueTask.FromResult(new PriceQuote(0, costUsd, null));

        public void Invalidate()
        {
        }
    }
}
