#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Tasks;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Services.Briefs;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Briefs;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.Apps;
using ReelBolt.Shared.Workflows.Lint;
using ReelBolt.WorkflowEngine.Execution.Snapshots;
using ReelBolt.WorkflowEngine.Tests.Workflows.Lint;
using Xunit;
using static ReelBolt.WorkflowEngine.Tests.AppInputs.AppInputsTestKit;

namespace ReelBolt.WorkflowEngine.Tests.AppInputs;

/// <summary>
/// The permission to send media as a field of the app (docs/design/app-inputs.md "Permission to send media as an app
/// field"): the one shared definition of a step's need, the derived field, the validator's refusals, the binder, the run
/// gate per caller kind, the engine's snapshot binding and the linter's fix.
/// </summary>
public class MediaEgressPermissionTests
{
    private static readonly string Avatar = Guid.NewGuid().ToString("D");

    private const string SegmentOff =
        """{"version":1,"source":{"kind":"ProjectFile"},"transcription":"Off","trackObjects":[{"label":"the plate","method":"Segmentation"}],"allowSourceMediaEgress":false}""";

    private const string SegmentOn =
        """{"version":1,"source":{"kind":"ProjectFile"},"transcription":"Off","trackObjects":[{"label":"the plate","method":"Segmentation"}],"allowSourceMediaEgress":true}""";

    private const string InlinePlain =
        """{"planSource":"Inline","inlinePrompt":"A barista pours latte art","durationSeconds":6,"ratio":"16:9","takes":1,"maxClips":1,"maxSpendCredits":5,"allowSourceMediaEgress":false}""";

    private const string PlanSourced =
        """{"planSource":"StepRef","plan":{"from":"Step","stepOrder":1},"durationSeconds":6,"ratio":"16:9","takes":1,"maxClips":2,"maxSpendCredits":5,"allowSourceMediaEgress":false}""";

    private static string AvatarClip(bool on = false) =>
        $$"""{"planSource":"Inline","inlinePrompt":"A person waves","durationSeconds":6,"ratio":"16:9","takes":1,"maxClips":1,"maxSpendCredits":5,"avatarId":"{{Avatar}}","allowSourceMediaEgress":{{(on ? "true" : "false")}}}""";

    private static WorkflowStep Row(int order, StepType type, string config, string? label = null)
    {
        WorkflowStep step = new() { Id = Guid.NewGuid(), StepOrder = order, StepType = type, AgentDefinitionId = Guid.NewGuid(), Label = label };
        AppBindingStep.SetConfigJson(step, config);
        return step;
    }

    private static bool Flag(string? json) =>
        JsonDocument.Parse(json!).RootElement.TryGetProperty("allowSourceMediaEgress", out JsonElement value) && value.ValueKind == JsonValueKind.True;

    // ---------------------------------------------------------------- the one definition of a need

    public static TheoryData<StepType, string, MediaEgressNeedLevel, string?> Needs => new()
    {
        { StepType.VideoAnalyze, SegmentOff, MediaEgressNeedLevel.Required, MediaEgressNeed.TracksAnObjectByName },
        { StepType.VideoAnalyze, """{"version":1,"trackObjects":[{"label":"plate","method":1}]}""", MediaEgressNeedLevel.Required, MediaEgressNeed.TracksAnObjectByName },
        { StepType.VideoAnalyze, """{"version":1,"trackObjects":[{"label":"plate","method":"Feature"}]}""", MediaEgressNeedLevel.None, null },
        { StepType.VideoGenerate, """{"planSource":"Inline","firstFrame":{"from":"Step","stepOrder":1}}""", MediaEgressNeedLevel.Required, MediaEgressNeed.StartsFromFrame },
        { StepType.VideoGenerate, """{"planSource":"Inline","avatarAudio":{"from":"Step","stepOrder":2}}""", MediaEgressNeedLevel.None, null },
        { StepType.VideoGenerate, PlanSourced, MediaEgressNeedLevel.Optional, MediaEgressNeed.PlannedFramesAndSubjects },
        { StepType.VideoGenerate, """{"planSource":"StepRef","stepOrder":1,"plannedSubjects":"Auto"}""", MediaEgressNeedLevel.Optional, MediaEgressNeed.PlannedFramesAndSubjects },
        { StepType.VideoGenerate, """{"planSource":"StepRef","stepOrder":1,"plannedSubjects":"Off"}""", MediaEgressNeedLevel.Optional, MediaEgressNeed.PlannedFrames },
        { StepType.VideoGenerate, InlinePlain, MediaEgressNeedLevel.None, null },
        { StepType.VideoEnhance, """{"op":"Upscale","engine":"Hosted"}""", MediaEgressNeedLevel.Required, MediaEgressNeed.EnhancesVideoWithAi },
        { StepType.VideoEnhance, """{"op":"Denoise","engine":"Local"}""", MediaEgressNeedLevel.None, null },
        { StepType.AudioCleanup, """{"target":"IsolateVoice"}""", MediaEgressNeedLevel.Required, MediaEgressNeed.CleansAudioWithAi },
        { StepType.AudioCleanup, """{"target":"RemoveMusic"}""", MediaEgressNeedLevel.Required, MediaEgressNeed.CleansAudioWithAi },
        { StepType.AudioCleanup, """{"target":"Hum"}""", MediaEgressNeedLevel.None, null },
        { StepType.VideoCompile, """{"mode":"Reencode"}""", MediaEgressNeedLevel.None, null },
    };

    [Fact]
    public void The_planned_reason_says_avatars_only_when_planned_subjects_can_show_them()
    {
        // Subjects off: today's sentence, byte for byte.
        MediaEgressNeed.Sends(MediaEgressNeed.PlannedFrames).Should().Be("may start a clip from a frame of your footage");
        MediaEgressNeed.ShortReason(MediaEgressNeed.PlannedFrames).Should().Be("can start a clip from your footage");
        // Subjects on (the default): the permission also lets the planner send the pictures of the project's avatars.
        string on = MediaEgressNeed.Sends(MediaEgressNeed.PlannedFramesAndSubjects);
        on.Should().StartWith("may start a clip from a frame of your footage").And.Contain("people and objects").And.Contain("pictures").And.Contain("outside AI company");
        MediaEgressNeed.ShortReason(MediaEgressNeed.PlannedFramesAndSubjects).Should().Contain("avatars");
    }

    [Theory]
    [MemberData(nameof(Needs))]
    public void A_steps_need_follows_its_own_settings(StepType type, string config, MediaEgressNeedLevel level, string? reason)
    {
        MediaEgressStepNeed need = MediaEgressNeed.For(Step(1, type, config));
        need.Level.Should().Be(level);
        need.Reason.Should().Be(reason);
    }

    [Fact]
    public void An_inline_generate_step_needs_it_when_the_run_chooses_an_avatar()
    {
        MediaEgressNeed.For(Step(1, StepType.VideoGenerate, InlinePlain), MediaEgressBriefAvatar.None).Level.Should().Be(MediaEgressNeedLevel.None);
        MediaEgressNeed.For(Step(1, StepType.VideoGenerate, InlinePlain), MediaEgressBriefAvatar.Offered).Level.Should().Be(MediaEgressNeedLevel.Optional);
        MediaEgressNeed.For(Step(1, StepType.VideoGenerate, InlinePlain), MediaEgressBriefAvatar.Chosen).Level.Should().Be(MediaEgressNeedLevel.Required);
        MediaEgressNeed.For(Step(1, StepType.VideoGenerate, AvatarClip())).Should().Match<MediaEgressStepNeed>(n =>
            n.Level == MediaEgressNeedLevel.Required && n.Reason == MediaEgressNeed.ShowsAnAvatar && !n.StoredOn && n.Pending);
        MediaEgressNeed.For(Step(1, StepType.VideoGenerate, AvatarClip(on: true))).Pending.Should().BeFalse();
    }

    [Fact]
    public void Every_step_config_with_the_switch_is_classified()
    {
        // Drift guard: a config type that gains AllowSourceMediaEgress must get an arm in MediaEgressNeed.For.
        List<StepType> carrying = [.. Enum.GetValues<StepType>().Where(t =>
            AppBindableCatalog.ConfigTypeOf(t)?.GetProperty("AllowSourceMediaEgress", BindingFlags.Public | BindingFlags.Instance) is not null)];

        carrying.Should().NotBeEmpty();
        carrying.Should().BeEquivalentTo(MediaEgressNeed.StepTypes);
        // And every classified type is one whose config the app logic can read and write.
        foreach (StepType type in MediaEgressNeed.StepTypes)
        {
            WorkflowStep step = new() { StepType = type };
            AppBindingStep.SetConfigJson(step, "{}");
            AppBindingStep.ConfigJsonOf(step).Should().Be("{}", $"{type} must be readable and writable by AppBindingStep");
        }
    }

    [Fact]
    public void The_config_member_is_still_not_bindable_by_an_app_input()
    {
        // The permission is bound by code only: no author- or assistant-written binding can name the member.
        foreach (StepType type in MediaEgressNeed.StepTypes)
            AppBindableCatalog.Find(type, MediaEgressNeed.ConfigMember).Should().BeNull();

        AppBindingStep bound = Step(1, StepType.VideoGenerate,
            """{"planSource":"Inline","inlinePrompt":"x","allowSourceMediaEgress":false,"appInputs":{"allowSourceMediaEgress":"anySwitch"}}""");
        AppBindingAnalysis.Analyze(Manifest(Input("anySwitch", "toggle")), [bound]).Problems
            .Should().ContainSingle().Which.Message.Should().Contain("cannot be set from the app");
    }

    // ---------------------------------------------------------------- linter agreement and fix

    [Fact]
    public async Task WL015_fires_exactly_where_the_shared_definition_says_required_and_off()
    {
        foreach ((StepType type, string config, MediaEgressNeedLevel level, string? _) in Needs.Select(row => ((StepType)row[0], (string)row[1], (MediaEgressNeedLevel)row[2], (string?)row[3])))
        {
            IReadOnlyList<LintFinding> findings = await LintKit.LintAsync([Row(1, type, config)]);
            MediaEgressStepNeed need = MediaEgressNeed.For(Step(1, type, config));
            findings.Rule("WL015").Any().Should().Be(need is { Level: MediaEgressNeedLevel.Required, StoredOn: false }, $"{type} {config}");
            level.Should().Be(need.Level);
        }
    }

    [Fact]
    public async Task WL015_sends_the_person_to_the_apps_own_field_and_is_quiet_when_the_run_gives_it()
    {
        IReadOnlyList<LintFinding> findings = await LintKit.LintAsync([LintKit.Of(1, StepType.VideoAnalyze, SegmentOff)]);
        LintFinding finding = findings.Rule("WL015").Should().ContainSingle().Subject;
        finding.Fix!.Kind.Should().Be(LintFixKind.FillInput);
        finding.Fix.LabelId.Should().Be("WL015.allowOnAppPage");
        finding.Args.Should().Contain("inputId", MediaEgressPermission.InputId);
        StepCompatibilityRules.Get("WL015").FixKind.Should().Be(LintFixKind.FillInput);

        RunBrief granted = new(Inputs: Values((MediaEgressPermission.InputId, "true")));
        (await LintKit.LintAsync([LintKit.Of(1, StepType.VideoAnalyze, SegmentOff)], granted)).Rule("WL015").Should().BeEmpty();
    }

    // ---------------------------------------------------------------- the derived field

    [Fact]
    public void The_field_is_required_where_a_step_cannot_run_without_it()
    {
        AppBindingStep[] steps = [Step(1, StepType.VideoAnalyze, SegmentOff, name: "Analyze video"), Step(2, StepType.VideoGenerate, AvatarClip(), name: "Generate Video")];

        AppManifest page = MediaEgressPermission.WithField(null, steps)!;

        AppManifestInput field = page.Inputs.Should().ContainSingle().Subject;
        AppManifestJson.Serialize(new AppManifest(Version: 2, Inputs: [field])).Should().Be(
            """{"version":2,"inputs":[{"kind":"input","label":"Allow sending your media to AI services","id":"allowMediaEgress","type":"toggle","help":"Needed by: Analyze video (finds the object), Generate Video (shows an avatar)","required":true,"more":false,"labelKey":"permission.mediaEgress.label","permission":"mediaEgress"}]}""");
        AppInputPlacement.IsUnderMore(field).Should().BeFalse("the permission never sits under More options");
        page.Version.Should().Be(AppManifestVocabulary.Version2);
    }

    [Fact]
    public void The_field_is_an_ordinary_switch_where_it_only_unlocks_something()
    {
        AppManifestInput field = MediaEgressPermission.WithField(null, [Step(4, StepType.VideoGenerate, PlanSourced, name: "Generate b-roll")])!.Inputs!.Single();

        AppManifestJson.Serialize(new AppManifest(Version: 2, Inputs: [field])).Should().Be(
            """{"version":2,"inputs":[{"kind":"input","label":"Allow sending your media to AI services","id":"allowMediaEgress","type":"toggle","help":"Used by: Generate b-roll (can start a clip from your footage or show your avatars)","more":false,"labelKey":"permission.mediaEgress.label","permission":"mediaEgress"}]}""");
        field.Required.Should().BeNull();
        field.Default.Should().BeNull("off unless the person switches it on");
    }

    [Fact]
    public void The_field_is_shown_on_when_every_step_already_allows_it_and_absent_when_nothing_sends()
    {
        AppManifestInput on = MediaEgressPermission.WithField(null, [Step(1, StepType.VideoAnalyze, SegmentOn, name: "Analyze")])!.Inputs!.Single();
        on.Default!.Value.ValueKind.Should().Be(JsonValueKind.True);
        on.Required.Should().BeNull();
        on.Help.Should().StartWith("Already allowed for: Analyze");

        AppManifest untouched = Manifest(Input("mood", "shortText"));
        MediaEgressPermission.WithField(untouched, [Step(1, StepType.VideoGenerate, InlinePlain)]).Should().BeSameAs(untouched,
            "a workflow with no such step is exactly what it was");
        MediaEgressPermission.WithField(null, [Step(1, StepType.VideoCompile, "{}")]).Should().BeNull();
    }

    [Fact]
    public void A_stored_page_gets_the_field_on_read_and_a_hand_written_one_is_replaced()
    {
        AppBindingStep[] steps = [Step(1, StepType.VideoAnalyze, SegmentOff)];
        AppManifest stored = Manifest(Input("mood", "shortText"));

        MediaEgressPermission.WithField(stored, steps)!.Inputs!.Select(i => i.Id).Should().Equal("mood", MediaEgressPermission.InputId);

        // A row that carries a forged copy (default on, another label) is read as the derived field, never as written.
        AppManifest forged = Manifest(
            Input("mood", "shortText"),
            Input(MediaEgressPermission.InputId, "toggle", "Sure", def: "true") with { Permission = MediaEgressPermission.Marker });
        AppManifestInput read = MediaEgressPermission.WithField(forged, steps)!.Inputs!.Single(i => i.Id == MediaEgressPermission.InputId);
        read.Default.Should().BeNull();
        read.Label.Should().Be(MediaEgressPermission.Label);
        read.Required.Should().Be(true);

        // The avatar picker on the page makes an inline generate step a possible sender.
        AppManifest withAvatar = new(Inputs: [new AppManifestInput(AppManifestVocabulary.KindBrief, Field: BriefFieldConsumers.Avatar, Label: "Avatar")]);
        MediaEgressPermission.WithField(withAvatar, [Step(1, StepType.VideoGenerate, InlinePlain)])!.Inputs!
            .Should().Contain(i => i.Id == MediaEgressPermission.InputId && i.Required == null);
    }

    // ---------------------------------------------------------------- shipped templates

    private static (AppManifest? Before, AppManifest? After, MediaEgressSummary Needs) PageOf(WorkflowTemplateDefinition template)
    {
        IReadOnlyList<AppBindingStep> steps = AppBindingStep.FromTemplate(template);
        AppManifest before = AppManifestDeriver.Effective(
            template.AppManifest, AppStepFacts.FromTemplateIgnoringSwitch(template), template.RequiresUserInput, template.Name);
        return (before, MediaEgressPermission.WithField(before, steps), MediaEgressPermission.Describe(steps, MediaEgressPermission.AvatarOf(before)));
    }

    [Fact]
    public void Templates_that_send_media_show_the_field_and_every_other_template_is_untouched()
    {
        List<string> showing = [];
        foreach (WorkflowTemplateDefinition template in WorkflowTemplateCatalog.GetAll())
        {
            (AppManifest? before, AppManifest? after, MediaEgressSummary needs) = PageOf(template);
            if (!needs.Present)
            {
                after.Should().BeSameAs(before, $"{template.Key} sends nothing, so its page is exactly what it was");
                continue;
            }

            showing.Add(template.Key);
            AppManifestInput field = after!.Inputs!.Single(MediaEgressPermission.IsField);
            after.Inputs!.Last().Should().BeSameAs(field, "the permission sits last, in its own block");
            after.Inputs!.Where(i => !MediaEgressPermission.IsField(i)).Should().Equal(before!.Inputs ?? [], "every other field is untouched");
            field.Required.Should().Be(needs.Required ? true : null, template.Key);
            // The template catalog itself is never rewritten: no shipped step binds the switch, no shipped page stores the field.
            (template.AppManifest?.Inputs ?? []).Should().NotContain(i => MediaEgressPermission.IsReserved(i));
        }

        // The object-effect recipe cannot run without it (its segmentation step ships with the switch off).
        showing.Should().Contain(WorkflowTemplateCatalog.RegionEffectTemplateKey);
        PageOf(WorkflowTemplateCatalog.GetByKey(WorkflowTemplateCatalog.RegionEffectTemplateKey)!).Needs.Required.Should().BeTrue();
        // The avatar clip offers an avatar: optional on the page, required for a run that chose one.
        showing.Should().Contain(WorkflowTemplateCatalog.AvatarClipTemplateKey);
        PageOf(WorkflowTemplateCatalog.GetByKey(WorkflowTemplateCatalog.AvatarClipTemplateKey)!).Needs.Required.Should().BeFalse();
        // The plain promo sends nothing.
        showing.Should().NotContain(WorkflowTemplateCatalog.StarterTemplateKey);
    }

    // ---------------------------------------------------------------- the validator

    [Fact]
    public void Nobody_writes_binds_defaults_or_renames_the_field_by_hand()
    {
        AppStepFacts[] facts = [new(StepType.VideoAnalyze)];
        AppBindingStep[] steps = [Step(1, StepType.VideoAnalyze, SegmentOff)];

        // An echoed copy of the system field is dropped on save (it is derived on every read).
        AppManifest echoed = MediaEgressPermission.WithField(null, steps)!;
        AppManifest clean = AppManifestValidator.Sanitize(echoed);
        clean.Inputs.Should().BeEmpty();
        AppManifestValidator.Validate(clean, facts, steps).Should().BeEmpty();

        // The reserved id without the marker, the marker on another input, and an unknown marker are all refused.
        AppManifest plainSwitch = Manifest(Input(MediaEgressPermission.InputId, "toggle", "Allow it", def: "true"));
        AppManifest markedElsewhere = Manifest(Input("anything", "toggle") with { Permission = MediaEgressPermission.Marker });
        AppManifest unknownMarker = Manifest(Input("anything", "toggle") with { Permission = "everything" });
        foreach (AppManifest manifest in new[] { plainSwitch, markedElsewhere, unknownMarker })
        {
            AppManifestValidator.Validate(AppManifestValidator.Sanitize(manifest), facts, steps)
                .Should().Contain(AppManifestValidator.ReservedPermissionMessage);
        }
    }

    [Fact]
    public void Every_save_path_shares_the_refusal_and_a_step_cannot_bind_the_switch()
    {
        // REST, the builder and the assistant (appManifest on Propose/Save, setAppInput, setAppPage) all go through this check.
        AppStepFacts[] facts = [new(StepType.VideoAnalyze)];
        AppBindingStep[] steps = [Step(1, StepType.VideoAnalyze, SegmentOff)];
        AppManifest forged = Manifest(Input(MediaEgressPermission.InputId, "toggle", "Allow it", def: "true"));

        (_, IReadOnlyList<string> errors) = WorkflowAppSupport.CheckForSave(forged, null, facts, steps);
        errors.Should().Contain(AppManifestValidator.ReservedPermissionMessage);

        // bindAppInput and a hand-written appInputs map: the member is not on the allowlist, for any input.
        AppBindingStep binds = Step(1, StepType.VideoAnalyze,
            """{"version":1,"trackObjects":[{"label":"the plate","method":"Segmentation"}],"allowSourceMediaEgress":false,"appInputs":{"allowSourceMediaEgress":"allowMediaEgress"}}""");
        AppBindingAnalysis.Analyze(MediaEgressPermission.WithField(null, steps), [binds]).Problems.Should().NotBeEmpty();
    }

    // ---------------------------------------------------------------- the binder

    [Fact]
    public void A_grant_turns_the_switch_on_only_where_a_step_needs_it()
    {
        AppBindingStep[] steps =
        [
            Step(1, StepType.VideoAnalyze, SegmentOff),
            Step(2, StepType.VideoGenerate, InlinePlain),
            Step(3, StepType.VideoGenerate, AvatarClip(on: true)),
            Step(4, StepType.VideoEnhance, """{"op":"Denoise","engine":"Local"}"""),
        ];

        AppInputBindResult bound = AppInputBinder.Bind(new AppManifest(), steps, Values((MediaEgressPermission.InputId, "true")));

        bound.IsValid.Should().BeTrue();
        Flag(bound.Steps[0].Step.ConfigJson).Should().BeTrue();
        bound.Steps[0].Changed.Should().BeTrue();
        AppInputBinder.CacheComponent(bound.Steps[0]).Should().Contain(MediaEgressPermission.InputId);
        // No need, already on, or a local step: untouched, and their cache keys are what they were.
        foreach (int untouched in new[] { 1, 2, 3 })
        {
            bound.Steps[untouched].Changed.Should().BeFalse();
            bound.Steps[untouched].Step.Should().BeSameAs(steps[untouched]);
            AppInputBinder.CacheComponent(bound.Steps[untouched]).Should().BeNull();
        }
    }

    [Fact]
    public void Off_or_absent_keeps_the_saved_value_and_never_switches_a_saved_true_off()
    {
        AppBindingStep[] steps = [Step(1, StepType.VideoAnalyze, SegmentOff), Step(2, StepType.VideoAnalyze, SegmentOn)];

        foreach (Dictionary<string, JsonElement> values in new[] { Values(), Values((MediaEgressPermission.InputId, "false")) })
        {
            AppInputBindResult bound = AppInputBinder.Bind(new AppManifest(), steps, values);
            bound.Steps.Should().OnlyContain(s => !s.Changed && s.Consumed.Count == 0);
            Flag(bound.Steps[0].Step.ConfigJson).Should().BeFalse();
            Flag(bound.Steps[1].Step.ConfigJson).Should().BeTrue();
        }
    }

    [Fact]
    public void A_grant_reaches_an_inline_generate_step_only_when_the_run_chose_an_avatar()
    {
        AppBindingStep[] steps = [Step(1, StepType.VideoGenerate, InlinePlain)];
        Dictionary<string, JsonElement> granted = Values((MediaEgressPermission.InputId, "true"));

        Flag(AppInputBinder.Bind(new AppManifest(), steps, granted).Steps[0].Step.ConfigJson).Should().BeFalse();
        Flag(AppInputBinder.Bind(new AppManifest(), steps, granted, briefAvatar: true).Steps[0].Step.ConfigJson).Should().BeTrue();
    }

    [Fact]
    public void The_checker_stores_only_a_grant_and_never_defaults_it_on()
    {
        AppBindingStep[] steps = [Step(1, StepType.VideoAnalyze, SegmentOn)];
        AppManifest page = MediaEgressPermission.WithField(null, steps)!;
        page.Inputs!.Single().Default.Should().NotBeNull("the page shows it on");

        AppInputValueChecker.Check(page, steps, null).Values.Should().BeEmpty("a default is never a grant");
        AppInputValueChecker.Check(page, steps, Values((MediaEgressPermission.InputId, "false"))).Values.Should().BeEmpty();
        AppInputValueChecker.Check(page, steps, Values((MediaEgressPermission.InputId, "true"))).Values.Should().ContainKey(MediaEgressPermission.InputId);
        AppInputValueChecker.Check(page, steps, Values((MediaEgressPermission.InputId, "\"yes\""))).Problems
            .Should().ContainSingle().Which.Code.Should().Be(AppInputValueProblem.Invalid);
    }

    // ---------------------------------------------------------------- the run gate

    private static readonly MediaEgressGate FromApp = new(false, MediaEgressPermission.ByApp);

    [Fact]
    public void A_run_that_needs_it_is_refused_until_it_is_given()
    {
        WorkflowStep[] steps = [Row(1, StepType.VideoAnalyze, SegmentOff)];

        AppInputGateResult missing = AppInputValueValidator.Evaluate(null, steps, null, "notes", mediaEgress: FromApp);
        missing.Refused.Should().BeTrue();
        missing.Code.Should().Be(AppInputValueProblem.Missing);
        missing.InputId.Should().Be(MediaEgressPermission.InputId);
        missing.Message.Should().Be("Allow sending your media to AI services to run this app.");

        // Off is the same as absent; an estimate is never refused for it.
        AppInputValueValidator.Evaluate(null, steps, Values((MediaEgressPermission.InputId, "false")), null, mediaEgress: FromApp).Code
            .Should().Be(AppInputValueProblem.Missing);
        AppInputValueValidator.Evaluate(null, steps, null, null, forEstimate: true).Refused.Should().BeFalse();
    }

    [Fact]
    public void A_grant_from_the_app_binds_the_run_and_names_what_is_sent()
    {
        WorkflowStep[] steps = [Row(1, StepType.VideoAnalyze, SegmentOff, "Find the plate"), Row(2, StepType.VideoCompile, """{"mode":"Reencode"}""")];

        AppInputGateResult gate = AppInputValueValidator.Evaluate(null, steps, Values((MediaEgressPermission.InputId, "true")), "notes", mediaEgress: FromApp);

        gate.Refused.Should().BeFalse();
        gate.Applies.Should().BeTrue();
        gate.UserRequest.Should().Be("notes");
        gate.Inputs.Should().ContainKey(MediaEgressPermission.InputId);
        gate.MediaEgressBy.Should().Be(MediaEgressPermission.ByApp);
        MediaEgressPermission.Sent(gate.MediaEgress!).Should().ContainSingle().Which.Should().Be(("Find the plate", "tracks an object by name"));
        Flag(gate.BoundSteps!.Single(s => s.StepOrder == 1).VideoAnalyzeConfigJson).Should().BeTrue();
        Flag(steps[0].VideoAnalyzeConfigJson).Should().BeFalse("the saved step is never written");
        gate.BoundSteps!.Single(s => s.StepOrder == 2).Should().BeSameAs(steps[1]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("engine")]
    [InlineData("APP")]
    public void A_grant_from_a_caller_that_may_not_give_it_is_refused_fail_closed(string? grantedBy)
    {
        WorkflowStep[] steps = [Row(1, StepType.VideoAnalyze, SegmentOff)];
        Dictionary<string, JsonElement> values = Values((MediaEgressPermission.InputId, "true"));

        AppInputGateResult gate = grantedBy is null
            ? AppInputValueValidator.Evaluate(null, steps, values, null)
            : AppInputValueValidator.Evaluate(null, steps, values, null, mediaEgress: new MediaEgressGate(false, grantedBy));

        gate.Refused.Should().BeTrue();
        gate.Code.Should().Be(AppInputValueProblem.Invalid);
        gate.InputId.Should().Be(MediaEgressPermission.InputId);
        gate.Message.Should().StartWith("This permission can only be given in the app.");
    }

    [Theory]
    [InlineData(MediaEgressPermission.ByApp)]
    [InlineData(MediaEgressPermission.ByApiKey)]
    [InlineData(MediaEgressPermission.ByMcp)]
    [InlineData(MediaEgressPermission.ByAssistantCard)]
    public void Each_known_grantor_is_accepted_and_recorded(string grantedBy)
    {
        AppInputGateResult gate = AppInputValueValidator.Evaluate(
            null, [Row(1, StepType.VideoAnalyze, SegmentOff)], Values((MediaEgressPermission.InputId, "true")), null,
            mediaEgress: new MediaEgressGate(false, grantedBy));

        gate.Refused.Should().BeFalse();
        gate.MediaEgressBy.Should().Be(grantedBy);
    }

    [Fact]
    public void A_workflow_already_set_up_starts_as_before_for_every_caller()
    {
        // Every step that cannot run without it already has its own switch on: nothing is asked of anyone.
        WorkflowStep[] steps = [Row(1, StepType.VideoAnalyze, SegmentOn)];

        AppInputGateResult gate = AppInputValueValidator.Evaluate(null, steps, null, "notes");

        gate.Refused.Should().BeFalse();
        gate.Applies.Should().BeFalse("without a grant the run start is exactly what it was");
        gate.UserRequest.Should().Be("notes");

        // An optional use is never required either.
        AppInputValueValidator.Evaluate(null, [Row(1, StepType.VideoGenerate, PlanSourced)], null, null).Refused.Should().BeFalse();
    }

    [Fact]
    public void The_runs_own_avatar_makes_an_inline_generate_step_need_it()
    {
        WorkflowStep[] steps = [Row(1, StepType.VideoGenerate, InlinePlain)];

        AppInputValueValidator.Evaluate(null, steps, null, null, mediaEgress: new MediaEgressGate(false, MediaEgressPermission.ByApp)).Refused.Should().BeFalse();
        AppInputGateResult withAvatar = AppInputValueValidator.Evaluate(null, steps, null, null, mediaEgress: new MediaEgressGate(true, MediaEgressPermission.ByApp));
        withAvatar.Code.Should().Be(AppInputValueProblem.Missing);
        withAvatar.InputId.Should().Be(MediaEgressPermission.InputId);

        AppInputGateResult granted = AppInputValueValidator.Evaluate(
            null, steps, Values((MediaEgressPermission.InputId, "true")), null, mediaEgress: new MediaEgressGate(true, MediaEgressPermission.ByApp));
        Flag(granted.BoundSteps!.Single().VideoGenerateConfigJson).Should().BeTrue();
    }

    [Fact]
    public void Only_a_step_the_run_reaches_can_require_it()
    {
        // The switch input picks the branch: the segmenting step is reached only when "track" is on.
        AppManifest page = Manifest(Input("track", "toggle", "Track the plate"));
        WorkflowStep branch = new()
        {
            Id = Guid.NewGuid(),
            StepOrder = 1,
            StepType = StepType.Conditional,
            AgentDefinitionId = Guid.NewGuid(),
            ConditionExpression = "input.track",
            TrueBranchStepOrder = "2",
            FalseBranchStepOrder = "3"
        };
        WorkflowStep[] steps = [branch, Row(2, StepType.VideoAnalyze, SegmentOff), Row(3, StepType.VideoCompile, """{"mode":"Reencode"}""")];

        AppInputValueValidator.Evaluate(page, steps, Values(("track", "false")), null, mediaEgress: FromApp).Refused.Should().BeFalse();
        AppInputValueValidator.Evaluate(page, steps, Values(("track", "true")), null, mediaEgress: FromApp).Code.Should().Be(AppInputValueProblem.Missing);
    }

    [Fact]
    public void An_app_with_no_such_step_has_no_such_field()
    {
        AppInputGateResult gate = AppInputValueValidator.Evaluate(
            null, [Row(1, StepType.VideoCompile, """{"mode":"Reencode"}""")], Values((MediaEgressPermission.InputId, "true")), null, mediaEgress: FromApp);

        gate.Refused.Should().BeTrue();
        gate.Message.Should().Contain("has no input named");
    }

    [Fact]
    public void Retry_and_resume_rebind_the_runs_own_grant_whoever_retries()
    {
        WorkflowStep[] steps = [Row(1, StepType.VideoAnalyze, SegmentOff)];
        StoredRunBrief stored = new(Inputs: Values((MediaEgressPermission.InputId, "true")), AllowMediaEgressBy: MediaEgressPermission.ByApiKey);

        AppInputGateResult gate = AppInputValueValidator.Evaluate(null, steps, stored.Inputs, null, mediaEgress: MediaEgressGate.FromStored(stored));
        gate.Refused.Should().BeFalse();
        gate.MediaEgressBy.Should().Be(MediaEgressPermission.ByApiKey);

        // A stored value with no grantor beside it is not a grant.
        StoredRunBrief bare = stored with { AllowMediaEgressBy = null };
        AppInputValueValidator.Evaluate(null, steps, bare.Inputs, null, mediaEgress: MediaEgressGate.FromStored(bare)).Code
            .Should().Be(AppInputValueProblem.Invalid);
    }

    // ---------------------------------------------------------------- the engine's snapshot

    [Fact]
    public void The_engine_binds_a_grant_onto_the_snapshot_only_with_its_grantor_on_record()
    {
        Dictionary<string, JsonElement> values = Values((MediaEgressPermission.InputId, "true"));

        AppInputSnapshotBinding.GrantedValues(new StoredRunBrief(Inputs: values, AllowMediaEgressBy: MediaEgressPermission.ByMcp))
            .Should().ContainKey(MediaEgressPermission.InputId);
        AppInputSnapshotBinding.GrantedValues(new StoredRunBrief(Inputs: values)).Should().NotContainKey(MediaEgressPermission.InputId);
        AppInputSnapshotBinding.GrantedValues(new StoredRunBrief(Inputs: values, AllowMediaEgressBy: "someone")).Should().BeEmpty();

        WorkflowStep[] clones = [Row(1, StepType.VideoAnalyze, SegmentOff), Row(2, StepType.VideoAnalyze, SegmentOn)];
        AppInputSnapshotBinding.TryBindInPlace(null, clones, values, out IReadOnlyList<AppBindingProblem> problems).Should().BeTrue();
        problems.Should().BeEmpty();
        Flag(clones[0].VideoAnalyzeConfigJson).Should().BeTrue();

        // No grant: the clones are never touched (a stored true stays, a stored false stays).
        WorkflowStep[] plain = [Row(1, StepType.VideoAnalyze, SegmentOff)];
        AppInputSnapshotBinding.TryBindInPlace(null, plain, null, out _).Should().BeTrue();
        plain[0].VideoAnalyzeConfigJson.Should().Be(SegmentOff);
    }

    [Fact]
    public void The_step_cache_key_gains_a_component_only_on_the_step_a_grant_changed()
    {
        WorkflowStep[] authored = [Row(1, StepType.VideoAnalyze, SegmentOff), Row(2, StepType.VideoAnalyze, SegmentOn), Row(3, StepType.VideoCompile, """{"mode":"Reencode"}""")];
        WorkflowExecution granted = new()
        {
            RunBriefJson = RunBriefJson.Serialize(new StoredRunBrief(
                Inputs: Values((MediaEgressPermission.InputId, "true")), AllowMediaEgressBy: MediaEgressPermission.ByApp))
        };

        AppInputRunState state = AppInputSnapshotBinding.StateFor(granted, null, authored, authored)!;
        state.ComponentFor(1).Should().Contain(MediaEgressPermission.InputId);
        state.ComponentFor(2).Should().BeNull("its switch was already on: nothing changed for it");
        state.ComponentFor(3).Should().BeNull();
        state.ReachableStepOrders.Should().BeEquivalentTo(new[] { 1, 2, 3 });

        // No value, or a value with no grantor: no state at all, so every key is exactly what it was.
        AppInputSnapshotBinding.StateFor(new WorkflowExecution(), null, authored, authored).Should().BeNull();
        WorkflowExecution bare = new() { RunBriefJson = RunBriefJson.Serialize(new StoredRunBrief(Inputs: Values((MediaEgressPermission.InputId, "true")))) };
        AppInputSnapshotBinding.StateFor(bare, null, authored, authored).Should().BeNull();
    }

    // ---------------------------------------------------------------- who is the app

    [Fact]
    public void Only_the_signed_in_users_own_session_is_the_app()
    {
        static ClaimsPrincipal Principal(params Claim[] claims) => new(new ClaimsIdentity([new Claim("sub", Guid.NewGuid().ToString()), .. claims], "test"));

        MediaEgressCallers.AppSession(Principal()).Should().Be(MediaEgressPermission.ByApp);
        MediaEgressCallers.AppSession(null).Should().BeNull("an unknown caller is refused");
        MediaEgressCallers.AppSession(new ClaimsPrincipal(new ClaimsIdentity())).Should().BeNull("not signed in");
        MediaEgressCallers.AppSession(Principal(new Claim(AuthClaims.ApiKeyId, Guid.NewGuid().ToString()))).Should().BeNull("an API key");
        MediaEgressCallers.AppSession(Principal(new Claim(AuthClaims.Scope, "assistant.tools"))).Should().BeNull("an OAuth-scoped token");
        MediaEgressCallers.AppSession(Principal(new Claim(OnBehalfOfTokenIssuer.ActorClaim, "workflow-engine"))).Should().BeNull("the engine acting for a user");
    }

    // ---------------------------------------------------------------- run history

    [Fact]
    public void Run_history_shows_the_permission_as_a_field_with_who_allowed_it()
    {
        Dictionary<string, JsonElement> values = Values((MediaEgressPermission.InputId, "true"));

        IReadOnlyList<RunAppInputResponse> rows = RunAppInputView.Compose(null, values, new Dictionary<Guid, string>(), MediaEgressPermission.ByApiKey, "Nightly builds")!;

        RunAppInputResponse row = rows.Should().ContainSingle().Subject;
        row.Id.Should().Be(MediaEgressPermission.InputId);
        row.Label.Should().Be("Allow sending your media to AI services");
        row.ValueText.Should().Be("Allowed by API key Nightly builds");
        row.AllowedBy.Should().Be(MediaEgressPermission.ByApiKey);

        RunAppInputView.AllowedByWords(MediaEgressPermission.ByApp).Should().Be("Allowed in the app");
        RunAppInputView.AllowedByWords(MediaEgressPermission.ByMcp).Should().Be("Allowed by a connected tool");
        RunAppInputView.AllowedByWords(MediaEgressPermission.ByAssistantCard).Should().Be("Approved in the assistant");
        RunAppInputView.Compose(null, Values(), new Dictionary<Guid, string>()).Should().BeNull();
    }

    [Fact]
    public async Task The_scope_and_the_grantors_are_closed_vocabularies()
    {
        await Task.CompletedTask;
        ApiScopes.IsKnown(ApiScopes.MediaEgress).Should().BeTrue();
        ApiScopes.MediaEgress.Should().Be("media.egress");
        MediaEgressPermission.Grantors.Should().Equal("app", "api_key", "mcp", "assistant_card");
        AppManifestVocabulary.Permissions.Should().Equal("mediaEgress");
    }
}
