using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.DesignSystems;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.Models.Billing;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Billing;

/// <summary>
/// A plan refusal names the lowest catalogue plan that includes the feature (display only), on every surface that
/// returns the gate's body, and a run that will certainly reach a gated step is refused at the start.
/// </summary>
public sealed class PlanUnlockRefusalTests
{
    private const string TrialJson = """{"roomsAllowed":false,"videoGenAllowed":false,"audioGenAllowed":false,"avatarsAllowed":false,"segmentationAllowed":false,"audioCleanupAllowed":false,"videoEnhanceAllowed":false,"socialPublishingAllowed":false,"apiAccess":false,"maxOutputHeight":720}""";
    private const string CreatorJson = """{"roomsAllowed":false,"videoGenAllowed":false,"audioGenAllowed":true,"avatarsAllowed":false,"segmentationAllowed":true,"audioCleanupAllowed":true,"videoEnhanceAllowed":true,"socialPublishingAllowed":true,"apiAccess":true,"maxOutputHeight":1080}""";
    private const string ProJson = """{"roomsAllowed":true,"videoGenAllowed":true,"audioGenAllowed":true,"avatarsAllowed":true,"segmentationAllowed":true,"audioCleanupAllowed":true,"videoEnhanceAllowed":true,"socialPublishingAllowed":true,"apiAccess":true,"maxOutputHeight":2160}""";

    private static Plan Row(string key, string name, PlanKind kind, int order, string json) =>
        new() { Id = Guid.NewGuid(), Key = key, Name = name, Kind = kind, SortOrder = order, IsActive = true, EntitlementsJson = json };

    private static readonly Plan[] Catalogue =
    [
        Row("trial", "Trial", PlanKind.Trial, 0, TrialJson),
        Row("creator", "Creator", PlanKind.Subscription, 1, CreatorJson),
        Row("pro", "Pro", PlanKind.Subscription, 2, ProJson),
        Row("enterprise", "Enterprise", PlanKind.Contact, 3, "{}")
    ];

    private sealed class Plans(bool withCatalogue) : IPlanSource
    {
        public Task<Plan?> GetSubscribedPlanAsync(Guid organizationId, CancellationToken cancellationToken) => Task.FromResult<Plan?>(null);

        public Task<Plan?> GetPlanByKeyAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult<Plan?>(Catalogue.First(p => p.Key == key));

        public Task<IReadOnlyList<Plan>> GetCatalogueAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Plan>>(withCatalogue ? Catalogue : []);
    }

    private static PlanBackedOrganizationEntitlements Entitlements(bool withCatalogue = true) =>
        new(new Plans(withCatalogue), Options.Create(new BillingOptions { Enabled = true, TrialPlanKey = "trial" }), new TenancyOptions());

    private static async Task<OrganizationEntitlementSet> TrialSetAsync(bool withCatalogue = true) =>
        await Entitlements(withCatalogue).GetAsync(Guid.NewGuid());

    private static JsonElement Body(PlanGateViolation v) => JsonSerializer.SerializeToElement(v.ToBody());

    [Theory]
    [InlineData(StepType.VideoGenerate, "video_generation", "pro")]
    [InlineData(StepType.AudioGenerate, "audio_generation", "creator")]
    [InlineData(StepType.EditRoom, "rooms", "pro")]
    public async Task A_step_refusal_carries_its_feature_and_the_lowest_plan_that_includes_it(StepType step, string feature, string plan)
    {
        PlanGateViolation v = PlanGate.CheckStep(step, await TrialSetAsync())!;

        JsonElement body = Body(v);
        body.GetProperty("code").GetString().Should().Be("PLAN_NOT_ENTITLED");
        body.GetProperty("feature").GetString().Should().Be(feature);
        body.GetProperty("unlockPlan").GetString().Should().Be(plan);
        body.GetProperty("unlockPlanName").GetString().Should().Be(char.ToUpperInvariant(plan[0]) + plan[1..]);
        v.StatusCode.Should().Be(402);
    }

    [Theory]
    [InlineData(1080, "creator")]
    [InlineData(2160, "pro")]
    public async Task A_resolution_refusal_names_the_lowest_plan_that_renders_that_size(int chosen, string plan)
    {
        PlanGateViolation v = PlanGate.CheckResolution(chosen, await TrialSetAsync())!;

        v.Feature.Should().Be("resolution");
        v.UnlockPlan.Should().Be(plan);
    }

    [Fact]
    public async Task The_hosted_feature_gates_carry_the_plan_too()
    {
        OrganizationEntitlementSet set = await TrialSetAsync();

        PlanGate.CheckSegmentation(set)!.UnlockPlan.Should().Be("creator");
        PlanGate.CheckAudioCleanup(set)!.UnlockPlan.Should().Be("creator");
        PlanGate.CheckVideoEnhance(set)!.UnlockPlan.Should().Be("creator");
        PlanGate.CheckSocialPublishing(set)!.UnlockPlan.Should().Be("creator");
        PlanGate.CheckApiAccess(set)!.UnlockPlan.Should().Be("creator");
        PlanGate.CheckAvatars(set)!.UnlockPlan.Should().Be("pro");
    }

    [Fact]
    public async Task Without_a_catalogue_the_body_keeps_its_old_shape()
    {
        PlanGateViolation v = PlanGate.CheckStep(StepType.VideoGenerate, await TrialSetAsync(withCatalogue: false))!;

        JsonElement body = Body(v);
        body.TryGetProperty("unlockPlan", out _).Should().BeFalse();
        body.TryGetProperty("unlockPlanName", out _).Should().BeFalse();
        body.GetProperty("feature").GetString().Should().Be("video_generation");
    }

    [Fact]
    public async Task A_refusal_that_is_not_about_a_feature_gets_no_plan()
    {
        OrganizationEntitlementSet set = await TrialSetAsync() with { MaxConcurrentExecutions = 1, StorageGb = 1 };
        PlanGate.CheckConcurrency(5, set)!.UnlockPlan.Should().BeNull();
        PlanGate.CheckStorage(0, 100L * 1024 * 1024 * 1024, set)!.UnlockPlan.Should().BeNull();
    }

    [Fact]
    public async Task The_billing_summary_unlocks_list_each_missing_feature_once_with_the_lowest_plan()
    {
        IReadOnlyList<object> unlocks = PlanUnlocks.UnlocksFor(await TrialSetAsync());

        JsonElement list = JsonSerializer.SerializeToElement(unlocks);
        Dictionary<string, string> byFeature = list.EnumerateArray()
            .ToDictionary(e => e.GetProperty("feature").GetString()!, e => e.GetProperty("planKey").GetString()!);
        byFeature["video_generation"].Should().Be("pro");
        byFeature["audio_generation"].Should().Be("creator");
        byFeature["resolution"].Should().Be("creator");
        byFeature["avatars"].Should().Be("pro");
        PlanUnlocks.UnlocksFor(await TrialSetAsync(withCatalogue: false)).Should().BeEmpty();
        // A plan that has the feature is not told to upgrade for it.
        PlanUnlocks.UnlocksFor(PlanEntitlements.ToSet(Catalogue[2]) with { UpgradePlans = PlanUnlocks.OptionsFrom(Catalogue) })
            .Should().BeEmpty();
    }

    // ---- the run start ------------------------------------------------------------------------

    private sealed class FakeUser(Guid userId, Guid organizationId) : ICurrentUser
    {
        public Guid UserId { get; } = userId;
        public string Email => "t@example.com";
        public bool IsAuthenticated => true;
        public Guid? OrganizationId { get; } = organizationId;
        public OrganizationRole? OrgRole => OrganizationRole.Owner;
        public bool IsPlatformAdmin => false;
    }

    private sealed class Start
    {
        public InferenceApiDbContext Db { get; } =
            new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        public Project Project { get; }
        public Guid WorkflowId { get; } = Guid.NewGuid();
        public RunStartGate Gate { get; }

        public Start(OrganizationEntitlementSet set)
        {
            Guid userId = Guid.NewGuid();
            Guid orgId = TestTenancy.OrgOf(userId);
            Project = new Project { Id = Guid.NewGuid(), Name = "P", OwnerId = userId, OrganizationId = orgId };
            Db.Projects.Add(Project);
            Db.WorkflowDefinitions.Add(new WorkflowDefinition { Id = WorkflowId, Name = "W", ProjectId = Project.Id });
            Mock<IOrganizationEntitlements> entitlements = new();
            entitlements.Setup(e => e.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).Returns(new ValueTask<OrganizationEntitlementSet>(set));
            Mock<IDesignBuildEstimateGate> designBuild = new();
            designBuild
                .Setup(g => g.CheckAsync(It.IsAny<Guid?>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<double?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(DesignBuildGateResult.NotApplicable);
            designBuild
                .Setup(g => g.CheckRunAsync(It.IsAny<Guid?>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<double?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(DesignBuildGateResult.NotApplicable);
            Gate = new RunStartGate(Db, new FakeUser(userId, orgId), new PlanGateService(Db, entitlements.Object), null, designBuild.Object);
        }

        public Start Step(int order, StepType type, string? condition = null, string? whenTrue = null, string? whenFalse = null)
        {
            Db.WorkflowSteps.Add(new WorkflowStep
            {
                Id = Guid.NewGuid(),
                WorkflowDefinitionId = WorkflowId,
                StepOrder = order,
                StepType = type,
                ConditionExpression = condition,
                TrueBranchStepOrder = whenTrue,
                FalseBranchStepOrder = whenFalse
            });
            return this;
        }

        public async Task<ActionResult?> RunAsync(RunStartPricing? pricing = null, RunStartRun? run = null)
        {
            await Db.SaveChangesAsync();
            return await Gate.RefuseAsync(Project, WorkflowId, null, pricing ?? RunStartPricing.SavedWorkflow, null, CancellationToken.None, run);
        }
    }

    private static void AssertNotEntitled(ActionResult? result, string feature, string plan)
    {
        ObjectResult o = result.Should().BeAssignableTo<ObjectResult>().Subject;
        o.StatusCode.Should().Be(402);
        JsonElement body = JsonSerializer.SerializeToElement(o.Value);
        body.GetProperty("code").GetString().Should().Be("PLAN_NOT_ENTITLED");
        body.GetProperty("feature").GetString().Should().Be(feature);
        body.GetProperty("unlockPlan").GetString().Should().Be(plan);
    }

    [Fact]
    public async Task A_trial_run_that_will_reach_a_video_generation_step_is_refused_at_the_start()
    {
        Start s = new(await TrialSetAsync());
        s.Step(1, StepType.Agent).Step(2, StepType.VideoGenerate);

        AssertNotEntitled(await s.RunAsync(), "video_generation", "pro");
    }

    [Fact]
    public async Task A_run_of_steps_the_plan_includes_is_not_refused_by_the_plan()
    {
        Start s = new(await TrialSetAsync());
        s.Step(1, StepType.Agent).Step(2, StepType.TimelineRender);

        (await s.RunAsync()).Should().BeNull();
    }

    [Fact]
    public async Task A_plan_that_includes_video_generation_is_not_refused()
    {
        Start s = new(PlanEntitlements.ToSet(Catalogue[2]));
        s.Step(1, StepType.VideoGenerate);

        (await s.RunAsync()).Should().BeNull();
    }

    [Fact]
    public async Task A_straight_workflow_with_routing_is_left_to_the_engine()
    {
        Start s = new(await TrialSetAsync());
        s.Step(1, StepType.Agent, condition: "x").Step(2, StepType.VideoGenerate);

        (await s.RunAsync()).Should().BeNull();
    }

    private static HashSet<int> Orders(params int[] o) => [.. o];

    [Fact]
    public async Task An_input_decided_conditional_is_certain_whichever_way_it_was_decided()
    {
        // The shape of every real app: the expression is stored on the Conditional and the resolver decided it.
        Start s = new(await TrialSetAsync());
        s.Step(1, StepType.Conditional, condition: "input.x", whenTrue: "2", whenFalse: "3")
            .Step(2, StepType.VideoGenerate).Step(3, StepType.Agent);

        AssertNotEntitled(await s.RunAsync(new RunStartPricing(null, Orders(1, 2))), "video_generation", "pro");
        (await s.RunAsync(new RunStartPricing(null, Orders(1, 3)))).Should().BeNull();
        // Undecided with one gated side: the engine may take the other.
        (await s.RunAsync(new RunStartPricing(null, Orders(1, 2, 3)))).Should().BeNull();
    }

    [Fact]
    public async Task An_undecided_conditional_with_every_path_gated_is_refused()
    {
        Start s = new(await TrialSetAsync());
        s.Step(1, StepType.Conditional, condition: "input.x", whenTrue: "2", whenFalse: "3")
            .Step(2, StepType.AudioGenerate).Step(3, StepType.AudioGenerate);

        AssertNotEntitled(await s.RunAsync(new RunStartPricing(null, Orders(1, 2, 3))), "audio_generation", "creator");
    }

    [Fact]
    public async Task A_finish_here_conditional_ends_the_run_so_later_steps_are_not_reached()
    {
        Start s = new(await TrialSetAsync());
        s.Step(1, StepType.Conditional, condition: "input.x", whenTrue: "2", whenFalse: "4")
            .Step(2, StepType.Agent).Step(3, StepType.Conditional, whenTrue: "end").Step(4, StepType.VideoGenerate);

        // Decided to finish after step 2: step 4 is never reached.
        (await s.RunAsync(new RunStartPricing(null, Orders(1, 2, 3)))).Should().BeNull();
        // Decided to the gated branch.
        AssertNotEntitled(await s.RunAsync(new RunStartPricing(null, Orders(1, 4))), "video_generation", "pro");
    }

    private static List<WorkflowStep> TemplateSteps(string key) =>
        [.. ReelBolt.Shared.Workflows.WorkflowTemplateCatalog.GetByKey(key)!.Steps.Select((t, i) => new WorkflowStep
        {
            StepOrder = i + 1, StepType = t.StepType, ConditionExpression = t.ConditionExpression,
            TrueBranchStepOrder = t.TrueBranchStepOrder, FalseBranchStepOrder = t.FalseBranchStepOrder, LoopTargetStepOrder = t.LoopTargetStepOrder
        })];

    private static async Task<ActionResult?> RunTemplateAsync(string key, Dictionary<string, object> inputs)
    {
        Start s = new(await TrialSetAsync());
        List<WorkflowStep> steps = TemplateSteps(key);
        foreach (WorkflowStep st in steps) s.Step(st.StepOrder, st.StepType, st.ConditionExpression, st.TrueBranchStepOrder, st.FalseBranchStepOrder);
        Dictionary<string, JsonElement> values = inputs.ToDictionary(kv => kv.Key, kv => JsonSerializer.SerializeToElement(kv.Value));
        IReadOnlySet<int> reachable = ReelBolt.Shared.Workflows.Apps.StaticBranchResolver.ReachableStepOrders(
            ReelBolt.Shared.Workflows.Apps.AppBindingStep.FromTemplate(ReelBolt.Shared.Workflows.WorkflowTemplateCatalog.GetByKey(key)!), values);
        return await s.RunAsync(new RunStartPricing(null, reachable));
    }

    [Theory]
    [InlineData(true, "video_generation")]
    [InlineData(false, "video_generation")]
    public async Task The_shipped_avatar_explainer_is_refused_for_a_trial_plan_whichever_way_its_inputs_decide(bool ownWords, string feature)
    {
        AssertNotEntitled(await RunTemplateAsync("avatar-explainer", new() { ["ownWords"] = ownWords }), feature, "pro");
        // Undecided (the input is absent): both paths end in video generation.
        AssertNotEntitled(await RunTemplateAsync("avatar-explainer", []), feature, "pro");
    }

    [Theory]
    [InlineData("Music")]
    [InlineData("SoundEffect")]
    public async Task The_shipped_music_or_sound_effect_is_refused_for_a_trial_plan_both_ways(string kind)
    {
        AssertNotEntitled(await RunTemplateAsync("music-or-sound-effect", new() { ["kind"] = kind }), "audio_generation", "creator");
        AssertNotEntitled(await RunTemplateAsync("music-or-sound-effect", []), "audio_generation", "creator");
    }

    [Fact]
    public async Task A_plan_that_includes_the_feature_is_not_refused_on_the_shipped_templates()
    {
        Start s = new(PlanEntitlements.ToSet(Catalogue[2]));
        foreach (WorkflowStep st in TemplateSteps("avatar-explainer")) s.Step(st.StepOrder, st.StepType, st.ConditionExpression, st.TrueBranchStepOrder, st.FalseBranchStepOrder);

        (await s.RunAsync(new RunStartPricing(null, Orders(1, 2, 3, 4)))).Should().BeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_workflow_of_another_project_or_organization_is_not_judged_and_reveals_nothing(bool sameOrganization)
    {
        Start s = new(await TrialSetAsync());
        s.Step(1, StepType.Agent);
        Guid orgId = sameOrganization ? s.Project.OrganizationId : Guid.NewGuid();
        Project other = new() { Id = Guid.NewGuid(), Name = "O", OwnerId = Guid.NewGuid(), OrganizationId = orgId };
        Guid foreign = Guid.NewGuid();
        s.Db.Projects.Add(other);
        s.Db.WorkflowDefinitions.Add(new WorkflowDefinition { Id = foreign, Name = "F", ProjectId = other.Id });
        s.Db.WorkflowSteps.Add(new WorkflowStep { Id = Guid.NewGuid(), WorkflowDefinitionId = foreign, StepOrder = 1, StepType = StepType.VideoGenerate });
        await s.Db.SaveChangesAsync();

        // This project's route, the other workflow's id: exactly what it was before (no plan answer, the caller's 404 follows).
        (await s.Gate.RefuseAsync(s.Project, foreign, null, RunStartPricing.SavedWorkflow, null, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task A_hidden_kind_of_workflow_is_not_judged()
    {
        Start s = new(await TrialSetAsync());
        s.Db.WorkflowDefinitions.Local.First(w => w.Id == s.WorkflowId).Kind = WorkflowDefinitionKind.CompositionRevision;
        s.Step(1, StepType.VideoGenerate);

        (await s.RunAsync()).Should().BeNull();
    }

    [Fact]
    public async Task Retry_and_Resume_never_get_the_step_check()
    {
        Start s = new(await TrialSetAsync());
        s.Step(1, StepType.VideoGenerate);

        (await s.RunAsync(run: new RunStartRun(Guid.NewGuid(), null))).Should().BeNull();
    }

    [Fact]
    public void Trial_and_contact_plans_are_never_named_as_the_plan_to_upgrade_to()
    {
        IReadOnlyList<UpgradePlanOption> options = PlanUnlocks.OptionsFrom(Catalogue);

        options.Select(o => o.Key).Should().Equal("creator", "pro");
    }
}
