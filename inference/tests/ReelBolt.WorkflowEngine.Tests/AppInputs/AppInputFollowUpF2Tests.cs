using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Compositions;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Briefs;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Tenancy;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.Apps;
using ReelBolt.Shared.Workflows.Replay;
using ReelBolt.WorkflowEngine.Tests.Agents;
using Xunit;
using static ReelBolt.WorkflowEngine.Tests.AppInputs.AppInputsTestKit;

namespace ReelBolt.WorkflowEngine.Tests.AppInputs;

/// <summary>
/// Follow-up F2 (docs/design/app-inputs.md 28): an agent step with history holds and shows its history moved to the model the
/// app chose (the API's estimate and the engine's plan through the one shared planner); a kind of model the plan lacks is
/// marked on the cards and refused at the run gate by one rule; a recipe not created yet lists its model choices; a revision
/// of an app run keeps the agents' chosen models.
/// </summary>
public class AppInputFollowUpF2Tests
{
    private static readonly AgentDefinition Writer = new()
    {
        Id = Guid.Parse("33333333-0000-0000-0000-0000000000f2"),
        Name = "Scriptwriter",
        Description = "d",
        SystemPrompt = "p",
        AgentType = AgentType.ScriptwriterAgent,
        IsBuiltIn = true,
        CreatedAt = DateTime.UtcNow
    };

    private static readonly Guid ChatDear = Guid.Parse("22222222-0000-0000-0000-0000000000f2");

    private static WorkflowStep[] AgentRun() =>
    [
        new() { Id = Guid.NewGuid(), StepOrder = 1, StepType = StepType.Agent, AgentDefinitionId = Writer.Id, AgentDefinition = Writer }
    ];

    private static StepCreditSample[] History(long millis) =>
        [.. Enumerable.Repeat(new StepCreditSample(StepType.Agent, Writer.Id, millis), 5)];

    /// <summary>
    /// The live pricer for a run with a chosen dearer model: a chat quantity costs its tokens x 7 at the chosen model and x 1 at
    /// the agent's ordinary one (the baseline); every other quantity its seed price. Built from the keys the dry run asked for,
    /// exactly as both services build theirs.
    /// </summary>
    private static IProfileQuantityPricer Pricer(IReadOnlyList<WorkflowStep> steps, StepCreditSample[] history, int ratio = 7)
    {
        RecordingProfilePricer recorder = new();
        RunCreditHolds.Plan(steps, new StepCreditEstimator(history), null, ColdStartStepPricing.KnownAgentTypes(steps), recorder);
        Dictionary<ProfilePriceKey, ProfileQuote> quotes = [];
        foreach (ProfilePriceKey key in recorder.Requests)
        {
            quotes[key] = key.Target == ProfilePriceTarget.Chat
                ? new ProfileQuote((long)key.Quantity * (key.Baseline ? 1 : ratio) / 100, false)
                : new ProfileQuote(StepUsageProfiles.SeedFallbackMillis(key.Kind, key.Quantity, PricingDefaults.DefaultMarkup), false);
        }

        return new TableProfilePricer(quotes);
    }

    private static DisplayCreditInputs Display(IReadOnlyList<WorkflowStep> steps, StepCreditSample[] history) =>
        DisplayCreditInputs.Empty with
        {
            StepSamples = [.. history.Select(s => new DisplayStepSample(s.StepType, null, s.AgentDefinitionId!.Value, s.CreditsMillis))],
            AgentTypes = ColdStartStepPricing.KnownAgentTypes(steps)
        };

    [Fact]
    public void An_agent_step_with_history_holds_and_shows_its_history_at_the_chosen_model_on_both_sides()
    {
        WorkflowStep[] steps = AgentRun();
        StepCreditSample[] history = History(10_000);

        // No choice: the p90 of history, as before; the dry run asks for no baseline price at all.
        RecordingProfilePricer plain = new();
        RunHoldPlan before = RunCreditHolds.Plan(steps, new StepCreditEstimator(history), null, ColdStartStepPricing.KnownAgentTypes(steps), plain);
        before.HoldsMillis[0].Should().Be(10_000);
        plain.Requests.Should().NotContain(k => k.Baseline, "a run without a choice prices exactly what it did");

        using IDisposable chosen = AppAgentModelAmbient.Enter(new Dictionary<Guid, Guid> { [Writer.Id] = ChatDear });
        IProfileQuantityPricer pricer = Pricer(steps, history);
        AppModelPricing model = AppModelHoldScaling.For(steps[0], AgentType.ScriptwriterAgent, ColdStartStepPricing.KnownAgentTypes(steps), pricer)!.Value;
        model.Chosen.TypicalMillis.Should().BeGreaterThan(model.Baseline.TypicalMillis);

        long engine = RunCreditHolds.Plan(steps, new StepCreditEstimator(history), null, ColdStartStepPricing.KnownAgentTypes(steps), pricer).HoldsMillis[0];
        WorkflowCostEstimate api = WorkflowCostEstimator.EstimateFrom(history, steps, null, Display(steps, history), pricer);

        engine.Should().Be(model.HoldFrom(10_000));
        engine.Should().BeGreaterThan(10_000, "a dearer chosen model holds more than the old model's history");
        api.RequiredMillis.Should().Be(engine, "the API's 402 and the engine's reservation are the same plan");
        api.Steps.Single().HoldMillis.Should().Be(engine);
        api.Steps.Single().TypicalMillis.Should().Be(model.Scale(10_000), "the history shown is moved by the same ratio");
    }

    [Fact]
    public void A_cheaper_chosen_model_holds_less_but_never_below_its_own_profile()
    {
        WorkflowStep[] steps = AgentRun();
        StepCreditSample[] history = History(10_000_000);
        using IDisposable chosen = AppAgentModelAmbient.Enter(new Dictionary<Guid, Guid> { [Writer.Id] = ChatDear });

        // The chosen model is the CHEAPER one here: its chat costs a seventh of the ordinary model's.
        RecordingProfilePricer recorder = new();
        RunCreditHolds.Plan(steps, new StepCreditEstimator(history), null, ColdStartStepPricing.KnownAgentTypes(steps), recorder);
        Dictionary<ProfilePriceKey, ProfileQuote> quotes = recorder.Requests.ToDictionary(
            k => k,
            k => k.Target == ProfilePriceTarget.Chat
                ? new ProfileQuote((long)k.Quantity * (k.Baseline ? 7 : 1), false)
                : new ProfileQuote(0, false));
        TableProfilePricer pricer = new(quotes);

        AppModelPricing model = AppModelHoldScaling.For(steps[0], AgentType.ScriptwriterAgent, ColdStartStepPricing.KnownAgentTypes(steps), pricer)!.Value;
        long hold = RunCreditHolds.Plan(steps, new StepCreditEstimator(history), null, ColdStartStepPricing.KnownAgentTypes(steps), pricer).HoldsMillis[0];

        hold.Should().BeLessThan(10_000_000, "a cheaper model's run holds less than the old model's history");
        hold.Should().BeGreaterThanOrEqualTo(model.Chosen.P75Millis, "never below what the chosen model needs per its profile");
        new AppModelPricing(model.Chosen, model.Baseline).HoldFrom(1).Should().Be(model.Chosen.P75Millis);
    }

    [Fact]
    public void A_choice_for_another_agent_moves_nothing()
    {
        WorkflowStep[] steps = AgentRun();
        using IDisposable chosen = AppAgentModelAmbient.Enter(new Dictionary<Guid, Guid> { [Guid.NewGuid()] = ChatDear });
        AppModelHoldScaling.For(steps[0], AgentType.ScriptwriterAgent, ColdStartStepPricing.KnownAgentTypes(steps), SeedProfilePricer.Instance)
            .Should().BeNull();
    }

    // ---------------- plan limits on model cards and at the run gate ----------------

    private static readonly OrganizationEntitlementSet Creator = OrganizationEntitlementSet.Unlimited with
    {
        VideoGenAllowed = false,
        AvatarsAllowed = false,
        AudioGenAllowed = true,
        MaxOutputHeight = 1080
    };

    [Fact]
    public void The_plan_rule_names_the_feature_a_model_kind_needs()
    {
        OrganizationEntitlementSet pro = OrganizationEntitlementSet.Unlimited with { AvatarsAllowed = true, MaxOutputHeight = 720 };
        AppModelRules.PlanFeature("VideoGeneration", AppModelRules.TextToVideo, Creator).Should().Be(PlanGate.VideoGenerationFeature);
        AppModelRules.PlanFeature("ImageEdit", AppModelRules.AvatarStill, Creator).Should().Be(PlanGate.VideoGenerationFeature);
        AppModelRules.PlanFeature("MusicGeneration", null, Creator).Should().BeNull();
        AppModelRules.PlanFeature("SoundEffectGeneration", null, Creator with { AudioGenAllowed = false }).Should().Be(PlanGate.AudioGenerationFeature);
        AppModelRules.PlanFeature("VideoGeneration", AppModelRules.TalkingHead, OrganizationEntitlementSet.Unlimited with { AvatarsAllowed = false })
            .Should().Be(PlanGate.AvatarsFeature);
        AppModelRules.PlanFeature("VideoGeneration", AppModelRules.TextToVideo, pro, clipShortSide: 768).Should().Be(PlanGate.ResolutionFeature);
        AppModelRules.PlanFeature("VideoGeneration", AppModelRules.TextToVideo, OrganizationEntitlementSet.Unlimited, clipShortSide: 768).Should().BeNull();
        AppModelRules.PlanFeature("Chat", null, Creator).Should().BeNull();
        AppModelRules.ClipShortSide(Step(1, StepType.VideoGenerate, """{"resolution":"1080P"}""")).Should().Be(1080);
    }

    private static readonly Guid Video = Guid.Parse("11111111-0000-0000-0000-0000000000f2");
    private const string Generate =
        """{"planSource":"Inline","inlinePrompt":"A barista","resolution":"768P","durationSeconds":6,"takes":1,"maxClips":1,"maxSpendCredits":5,"appInputs":{"@model":"videoModel"}}""";

    private static readonly AppManifest VideoApp = Manifest(Input("videoModel", "model", "Video model", capability: "VideoGeneration"));

    private static readonly IReadOnlyList<AppModelCandidate> Candidates =
        [new(Video, "VideoGeneration", "MiniMax", "MiniMax-H3", "Hailuo", IsPlatform: true, IsDefault: true)];

    private static WorkflowStep VideoStep() => new()
    {
        Id = Guid.NewGuid(),
        StepOrder = 1,
        StepType = StepType.VideoGenerate,
        AgentDefinitionId = Guid.NewGuid(),
        VideoGenerateConfigJson = Generate
    };

    [Fact]
    public void The_run_gate_refuses_a_model_the_plan_does_not_include_naming_the_field_and_the_feature()
    {
        AppInputGateResult refused = AppInputValueValidator.Evaluate(VideoApp, [VideoStep()], Values(("videoModel", $"\"{Video}\"")), null,
            candidates: Candidates, plan: Creator);
        refused.Code.Should().Be(AppInputValueValidator.ModelNotInPlanCode);
        refused.InputId.Should().Be("videoModel");
        refused.Feature.Should().Be(PlanGate.VideoGenerationFeature);
        refused.Message.Should().Contain("Your plan does not include video generation").And.Contain("Video model");
        JsonSerializer.Serialize(refused.ErrorBody()).Should().Contain("\"feature\":\"video_generation\"");

        AppInputValueValidator.Evaluate(VideoApp, [VideoStep()], Values(("videoModel", $"\"{Video}\"")), null,
            candidates: Candidates, plan: OrganizationEntitlementSet.Unlimited).Refused.Should().BeFalse();
        AppInputValueValidator.Evaluate(VideoApp, [VideoStep()], Values(("videoModel", $"\"{Video}\"")), null, candidates: Candidates)
            .Refused.Should().BeFalse("no plan read (billing off, no organization): nothing new is refused");
        AppInputValueValidator.Evaluate(VideoApp, [VideoStep()], null, null, candidates: Candidates, plan: Creator)
            .Refused.Should().BeFalse("no model chosen: the step's own plan gate stays the one that answers");
    }

    private sealed class FixedCandidates(IReadOnlyList<AppModelCandidate> candidates) : IAppInputModelResolverSource
    {
        public Task<IReadOnlyList<AppModelCandidate>> CandidatesAsync(Guid? organizationId, Guid projectId, CancellationToken ct) =>
            Task.FromResult(candidates);
    }

    private sealed class FixedPlan(OrganizationEntitlementSet plan) : IOrganizationEntitlements
    {
        public ValueTask<OrganizationEntitlementSet> GetAsync(Guid organizationId, CancellationToken cancellationToken = default) => ValueTask.FromResult(plan);
    }

    private static InferenceApiDbContext ApiDb() =>
        new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase("f2-" + Guid.NewGuid().ToString("N")).Options);

    [Fact]
    public async Task The_options_mark_every_model_of_a_kind_the_plan_lacks_with_its_feature()
    {
        await using InferenceApiDbContext db = ApiDb();
        Guid projectId = Guid.NewGuid();
        AppInputModelOptionsService service = new(db, new FixedCandidates(Candidates), entitlements: new FixedPlan(Creator));

        AppInputModelOptionsResponse options = (await service.OptionsForAsync(Guid.NewGuid(), projectId, VideoApp, [VideoStep()], "videoModel", CancellationToken.None))!;
        AppInputModelOption card = options.Options.Single();
        card.Available.Should().BeFalse();
        card.UnavailableCode.Should().Be(AppModelRules.NotInPlan);
        card.PlanFeature.Should().Be(PlanGate.VideoGenerationFeature);
        card.UnavailableReason.Should().Be("Your plan does not include video generation");
        options.Label.Should().Be("Video model");

        AppInputModelOptionsService open = new(db, new FixedCandidates(Candidates), entitlements: new FixedPlan(OrganizationEntitlementSet.Unlimited));
        (await open.OptionsForAsync(Guid.NewGuid(), projectId, VideoApp, [VideoStep()], "videoModel", CancellationToken.None))!
            .Options.Single().Available.Should().BeTrue();
    }

    [Fact]
    public async Task A_default_model_that_cannot_do_the_steps_job_is_not_the_recommended_one()
    {
        await using InferenceApiDbContext db = ApiDb();
        Guid lipSyncId = Guid.NewGuid();
        // The capability's default row is a MiniMax model; a talking-head step needs a fal.ai lip-sync endpoint.
        IReadOnlyList<AppModelCandidate> candidates =
        [
            .. Candidates,
            new(lipSyncId, "LipSync", "FalAi", "minimax/h3-max/lip-sync/image-to-video", "Lip-sync", IsPlatform: true, IsDefault: false)
        ];
        WorkflowStep talking = VideoStep();
        talking.VideoGenerateConfigJson = Generate.Replace("\"takes\":1", "\"takes\":1,\"avatarAudio\":{\"from\":\"Step\",\"stepOrder\":2}");
        AppInputModelOptionsService service = new(db, new FixedCandidates(candidates), entitlements: new FixedPlan(OrganizationEntitlementSet.Unlimited));

        AppInputModelOptionsResponse options = (await service.OptionsForAsync(Guid.NewGuid(), Guid.NewGuid(), VideoApp, [talking], "videoModel", CancellationToken.None))!;

        options.Options.Where(o => o.Recommended).Should().BeEmpty("the only default row cannot serve a talking head");
        options.Options.Should().NotContain(o => o.Id == Video, "a talking head is offered LipSync models only");
        options.Options.Single(o => o.Id == lipSyncId).Available.Should().BeTrue();
    }

    // ---------------- a recipe not created yet ----------------

    private static IConfiguration Switch(bool on) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["Avatars:Enabled"] = "true" })
        .Build();

    private sealed class OrgUser(Guid org) : ICurrentUser
    {
        public Guid UserId { get; } = Guid.NewGuid();
        public string Email => "f2@example.com";
        public bool IsAuthenticated => true;
        public bool IsPlatformAdmin => false;
        public Guid? OrganizationId => org;
        public OrganizationRole? OrgRole => OrganizationRole.Owner;
    }

    [Fact]
    public async Task GetWorkflowTemplate_lists_each_model_fields_choices_before_the_recipe_is_created()
    {
        await using InferenceApiDbContext db = ApiDb();
        foreach (AgentType type in Enum.GetValues<AgentType>())
            db.AgentDefinitions.Add(new AgentDefinition { Id = Guid.NewGuid(), Name = type.ToString(), Description = "d", SystemPrompt = "p", AgentType = type, IsBuiltIn = true, CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        AppModelCandidate lipSync = new(Guid.NewGuid(), "VideoGeneration", "FalAi", "minimax/h3-max/lip-sync/image-to-video", "Lip sync", true);
        AppModelCandidate still = new(Guid.NewGuid(), "ImageEdit", "FalAi", "google/nano-banana-2.1/edit", "Nano edit", true, IsDefault: true);
        AssistantKnowledgeTools tools = new(
            db, new FakePlatformDocsSearch(), new WorkflowTemplateProvisioningService(db, NullLogger<WorkflowTemplateProvisioningService>.Instance, Switch(true)),
            appInputOptions: new AppInputModelOptionsService(db, new FixedCandidates([.. Candidates, lipSync, still]), entitlements: new FixedPlan(Creator)),
            currentUser: new OrgUser(Guid.NewGuid()));
        AIFunction get = tools.GetTools(new AssistantToolContext(Guid.NewGuid(), null)).Single(t => t.Name == "GetWorkflowTemplate");

        object? raw = await get.InvokeAsync(new AIFunctionArguments { ["templateKey"] = WorkflowTemplateCatalog.AvatarClipTemplateKey });
        JsonElement template = JsonDocument.Parse(raw is JsonElement { ValueKind: JsonValueKind.String } s ? s.GetString()! : raw!.ToString()!).RootElement;

        JsonElement[] fields = [.. template.GetProperty("modelOptions").EnumerateArray()];
        fields.Select(f => f.GetProperty("inputId").GetString()).Should().BeEquivalentTo(["videoModel", "liteModel", "pictureModel"]);
        JsonElement picture = fields.Single(f => f.GetProperty("inputId").GetString() == "pictureModel");
        JsonElement option = picture.GetProperty("options").EnumerateArray().Single();
        option.GetProperty("name").GetString().Should().Be("Nano edit");
        option.GetProperty("available").GetBoolean().Should().BeFalse();
        option.GetProperty("unavailableReason").GetString().Should().Be("Your plan does not include video generation");
        template.GetRawText().Should().NotContain("costIndex", "the price is words, never a figure");

        // With the switch off the recipe has no model field and nothing is listed.
        AssistantKnowledgeTools off = new(
            db, new FakePlatformDocsSearch(), new WorkflowTemplateProvisioningService(db, NullLogger<WorkflowTemplateProvisioningService>.Instance, Switch(false)),
            appInputOptions: new AppInputModelOptionsService(db, new FixedCandidates(Candidates)));
        object? plain = await off.GetTools(new AssistantToolContext(Guid.NewGuid(), null)).Single(t => t.Name == "GetWorkflowTemplate")
            .InvokeAsync(new AIFunctionArguments { ["templateKey"] = WorkflowTemplateCatalog.SubtitlesTemplateKey });
        JsonDocument.Parse(plain is JsonElement { ValueKind: JsonValueKind.String } p ? p.GetString()! : plain!.ToString()!).RootElement
            .GetProperty("modelOptions").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task A_recipes_dry_app_is_its_page_and_steps()
    {
        await using InferenceApiDbContext db = ApiDb();
        foreach (AgentType type in Enum.GetValues<AgentType>())
            db.AgentDefinitions.Add(new AgentDefinition { Id = Guid.NewGuid(), Name = type.ToString(), Description = "d", SystemPrompt = "p", AgentType = type, IsBuiltIn = true, CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var on = await new WorkflowTemplateProvisioningService(db, NullLogger<WorkflowTemplateProvisioningService>.Instance, Switch(true))
            .BuildDryAppAsync(WorkflowTemplateCatalog.AvatarClipTemplateKey, CancellationToken.None);
        on.Should().NotBeNull();
        on!.Value.Steps.Should().NotBeEmpty();
        (on.Value.Manifest.Inputs ?? []).Count(i => i.Type == "model").Should().Be(3);

        (await new WorkflowTemplateProvisioningService(db, NullLogger<WorkflowTemplateProvisioningService>.Instance, Switch(true))
            .BuildDryAppAsync("nope", CancellationToken.None)).Should().BeNull();
    }

    // ---------------- composition revisions keep the agents' chosen models ----------------

    private static CompositionReplaySource ReplaySource(string? manifestJson, string? briefJson)
    {
        Guid definition = Guid.NewGuid();
        WorkflowStep writer = new() { Id = Guid.NewGuid(), WorkflowDefinitionId = definition, StepOrder = 1, StepType = StepType.Agent, AgentDefinitionId = Writer.Id };
        WorkflowStep film = new() { Id = Guid.NewGuid(), WorkflowDefinitionId = definition, StepOrder = 2, StepType = StepType.Agent, AgentDefinitionId = Guid.NewGuid() };
        WorkflowExecution run = new() { Id = Guid.NewGuid(), WorkflowDefinitionId = definition, RunBriefJson = briefJson };
        return new CompositionReplaySource(
            run, [writer, film], new Dictionary<int, Guid> { [1] = Guid.NewGuid(), [2] = Guid.NewGuid() }, 2, definition, "Promo", manifestJson);
    }

    [Fact]
    public void A_revision_of_an_app_run_that_chose_an_agent_model_carries_the_page_that_names_it()
    {
        AppManifest page = Manifest(Input("scriptModel", "model", "Script model", capability: "Chat", agent: Writer.Id));
        string manifestJson = AppManifestJson.Serialize(page);
        string chose = RunBriefJson.Serialize(new StoredRunBrief(Inputs: Values(("scriptModel", $"\"{ChatDear}\""))));

        CompositionReplaySource source = ReplaySource(manifestJson, chose);
        WorkflowDefinition replay = CompositionRevisionService.BuildReplayWorkflow(Guid.NewGuid(), source, source.Plan(CompositionCutPolicy.KeepCut), Guid.NewGuid());
        replay.Kind.Should().Be(WorkflowDefinitionKind.CompositionReplay);
        replay.AppManifestJson.Should().Be(manifestJson, "the engine lays the run's agent-model choices on the replayed agents from it");

        CompositionReplaySource noChoice = ReplaySource(manifestJson, RunBriefJson.Serialize(new StoredRunBrief(Inputs: Values(("other", "true")))));
        CompositionRevisionService.BuildReplayWorkflow(Guid.NewGuid(), noChoice, noChoice.Plan(CompositionCutPolicy.KeepCut), Guid.NewGuid())
            .AppManifestJson.Should().BeNull("a run that chose no agent model replays exactly as before");
        CompositionReplaySource noPage = ReplaySource(null, chose);
        CompositionRevisionService.BuildReplayWorkflow(Guid.NewGuid(), noPage, noPage.Plan(CompositionCutPolicy.KeepCut), Guid.NewGuid())
            .AppManifestJson.Should().BeNull();
    }

    // ---------------- the still's price tier ----------------

    [Fact]
    public void A_still_is_priced_under_the_endpoint_and_tier_the_call_is_metered_under()
    {
        VideoGenerateCreditPricing.StillBillingKey(InferenceProviderKind.FalAi, null).Should().Be(("google/nano-banana-2.1/edit", "1K"));
        VideoGenerateCreditPricing.StillBillingKey(InferenceProviderKind.FalAi, "google/nano-banana-2.1/edit").Should().Be(("google/nano-banana-2.1/edit", "1K"));
        VideoGenerateCreditPricing.StillBillingKey(InferenceProviderKind.FalAi, "blackforestlabs/flux-3/edit-image").Should().Be(("blackforestlabs/flux-3/edit-image", null));
        VideoGenerateCreditPricing.StillBillingKey(InferenceProviderKind.OpenAICompatible, "x").Should().Be(("x", (string?)null));
    }
}
