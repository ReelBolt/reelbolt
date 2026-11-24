using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Inference.Api.Services.Briefs;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Briefs;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.Apps;
using ReelBolt.Shared.Workflows.Lint;
using Xunit;
using static ReelBolt.WorkflowEngine.Tests.Workflows.Lint.LintKit;
using H = ReelBolt.WorkflowEngine.Tests.Avatars.AvatarTestHarness;

namespace ReelBolt.WorkflowEngine.Tests.Avatars;

/// <summary>
/// Several subjects per clip on the Inference API side (docs/design/avatar-objects.md "Subjects in a video generation
/// step"): the save check, the run brief, the linter (WL015, WL027 and the new WL034), the approval card's line, the
/// assistant's egress guard, the avatar usage report and the recipes listed per kind. In the avatar-switch collection
/// because the per-kind switches are process-wide.
/// </summary>
[Collection(AvatarSwitchCollection.Name)]
public class SubjectsApiTests : IDisposable
{
    private static readonly Guid ProjectId = Guid.NewGuid();
    private readonly InferenceApiDbContext _db = new(new DbContextOptionsBuilder<InferenceApiDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    public void Dispose()
    {
        AppStepFacts.UsePersonAvatarSwitch(null);
        AppStepFacts.UseObjectAvatarSwitch(null);
        _db.Dispose();
    }

    private async Task<ProjectAvatar> AddAsync(AvatarSubjectKind kind, string name, Action<ProjectAvatar>? tweak = null, Guid? project = null)
    {
        bool person = kind == AvatarSubjectKind.Person;
        var avatar = new ProjectAvatar
        {
            Id = Guid.NewGuid(),
            ProjectId = project ?? ProjectId,
            Name = name,
            SubjectKind = kind,
            ConsentAttestedByUserId = Guid.NewGuid(),
            ConsentAttestedAt = DateTime.UtcNow.AddDays(-1),
            ConsentStatement = person ? ReelBolt.Shared.Avatars.AvatarConsentStatement.English : ReelBolt.Shared.Avatars.AvatarObjectStatement.English,
            ConsentStatementVersion = person ? ReelBolt.Shared.Avatars.AvatarConsentStatement.Version : ReelBolt.Shared.Avatars.AvatarObjectStatement.Version,
            ConsentLocale = "en",
            ConsentSubjectName = person ? "Anna Example" : string.Empty,
            ConsentSubjectAdult = person,
            RetentionUntil = person ? DateTime.UtcNow.AddDays(30) : ProjectAvatar.NoRetention,
            DisclosureLabel = person ? AvatarDisclosure.Visible : AvatarDisclosure.MetadataOnly,
        };
        tweak?.Invoke(avatar);
        _db.ProjectAvatars.Add(avatar);
        await _db.SaveChangesAsync();
        return avatar;
    }

    private static string Subjects(bool egress, string extra, params Guid[] ids) =>
        "{\"planSource\":\"Inline\",\"inlinePrompt\":\"x\",\"maxSpendCredits\":5,\"subjects\":["
        + string.Join(",", ids.Select(id => "{\"avatarId\":\"" + id.ToString("D") + "\"}")) + "]"
        + (egress ? ",\"allowSourceMediaEgress\":true" : string.Empty) + extra + "}";

    private static CreateWorkflowStepRequest Step(string json) =>
        new(Guid.NewGuid(), 1, null, "clip", StepType: nameof(StepType.VideoGenerate), VideoGenerateConfigJson: json);

    private async Task<IReadOnlyList<string>> Save(string json)
    {
        List<CreateWorkflowStepRequest> steps = [Step(json)];
        IReadOnlyList<string> a = await VideoGenerateAvatarSaveValidator.ValidateAsync(_db, ProjectId, steps, CancellationToken.None);
        VideoGenerateAvatarSaveValidator.Validate(_db, ProjectId, steps).Should().Equal(a);
        return a;
    }

    // ---------------------------------------------------------------- the save check

    [Fact]
    public async Task A_step_may_be_saved_with_a_person_and_objects_of_its_own_project()
    {
        ProjectAvatar anna = await AddAsync(AvatarSubjectKind.Person, "Anna");
        ProjectAvatar machine = await AddAsync(AvatarSubjectKind.Object, "Machine");
        ProjectAvatar cup = await AddAsync(AvatarSubjectKind.Object, "Cup");

        (await Save(Subjects(true, "", anna.Id, machine.Id, cup.Id))).Should().BeEmpty();
    }

    [Fact]
    public async Task Every_subject_is_judged_another_projects_avatar_is_not_found_and_a_revoked_one_is_named()
    {
        ProjectAvatar machine = await AddAsync(AvatarSubjectKind.Object, "Machine");
        ProjectAvatar foreign = await AddAsync(AvatarSubjectKind.Object, "Elsewhere", project: Guid.NewGuid());
        ProjectAvatar revoked = await AddAsync(AvatarSubjectKind.Person, "Anna", a => a.ConsentRevokedAt = DateTime.UtcNow);

        IReadOnlyList<string> errors = await Save(Subjects(true, "", machine.Id, foreign.Id, revoked.Id));

        errors.Should().HaveCount(2);
        errors.Should().Contain(e => e.Contains(foreign.Id.ToString()) && e.EndsWith("was not found in this project.", StringComparison.Ordinal));
        errors.Should().Contain(e => e.Contains(revoked.Id.ToString()) && e.Contains("consent_revoked"));
    }

    [Fact]
    public async Task A_second_person_and_a_talking_head_that_is_not_one_person_are_refused_at_save()
    {
        ProjectAvatar anna = await AddAsync(AvatarSubjectKind.Person, "Anna");
        ProjectAvatar ben = await AddAsync(AvatarSubjectKind.Person, "Ben");
        ProjectAvatar machine = await AddAsync(AvatarSubjectKind.Object, "Machine");
        string audio = ",\"avatarAudioProjectFileId\":\"" + Guid.NewGuid().ToString("D") + "\"";

        (await Save(Subjects(true, "", anna.Id, ben.Id))).Should().ContainSingle().Which.Should().Contain("at most one person");
        (await Save(Subjects(true, audio, machine.Id))).Should().ContainSingle().Which.Should().Contain("a talking head");
        (await Save(Subjects(true, audio, anna.Id))).Should().BeEmpty("one person may speak");
        // The legacy way of naming an object as a talking head is judged the same.
        string legacy = "{\"planSource\":\"Inline\",\"inlinePrompt\":\"x\",\"maxSpendCredits\":5,\"avatarId\":\"" + machine.Id.ToString("D") + "\"" + audio + "}";
        (await Save(legacy)).Should().ContainSingle().Which.Should().Contain("a talking head");
    }

    /// <summary>A video-generation capability (account, model, capability) of the platform or of another organization.</summary>
    private async Task<Guid> AddVideoProviderAsync(InferenceProviderKind kind, Guid? organizationId = null)
    {
        var account = new InferenceProvider
        {
            Id = Guid.NewGuid(),
            Name = "video " + Guid.NewGuid().ToString("N")[..6],
            Kind = kind,
            Endpoint = "https://example.invalid",
            OrganizationId = organizationId
        };
        var model = new InferenceModel { Id = Guid.NewGuid(), ProviderId = account.Id, ModelCode = "m", DisplayName = "m " + account.Name, OrganizationId = organizationId };
        var capability = new ModelCapability
        {
            Id = Guid.NewGuid(),
            ModelId = model.Id,
            OrganizationId = organizationId,
            Capability = InferenceProviderCapability.VideoGeneration
        };
        _db.InferenceProviders.Add(account);
        _db.InferenceModels.Add(model);
        _db.ModelCapabilities.Add(capability);
        await _db.SaveChangesAsync();
        return capability.Id;
    }

    [Fact]
    public async Task A_subject_on_a_text_only_model_of_the_steps_own_provider_is_refused_at_save_naming_the_sibling()
    {
        ProjectAvatar machine = await AddAsync(AvatarSubjectKind.Object, "Machine");
        Guid fal = await AddVideoProviderAsync(InferenceProviderKind.FalAi);
        string On(string model, Guid provider) => ",\"providerId\":\"" + provider.ToString("D") + "\",\"model\":\"" + model + "\"";

        (await Save(Subjects(true, On("alibaba/wan-3.0/text-to-video", fal), machine.Id))).Should().ContainSingle().Which.Should().Be(
            "Step 1 (VideoGenerate): 'alibaba/wan-3.0/text-to-video' takes no pictures. Use 'alibaba/wan-3.0/image-to-video' to show an avatar or an object.");
        string legacy = "{\"planSource\":\"Inline\",\"inlinePrompt\":\"x\",\"maxSpendCredits\":5,\"avatarId\":\"" + machine.Id.ToString("D") + "\""
            + On("fal-ai/veo3.1", fal) + "}";
        (await Save(legacy)).Should().ContainSingle().Which.Should().Contain("Use 'fal-ai/veo3.1/reference-to-video'");

        // A model that takes pictures, a model with no declaration, the default provider, and another organization's
        // provider (never described here: the ownership check refuses it under its own message) all save.
        (await Save(Subjects(true, On("alibaba/wan-3.0/image-to-video", fal), machine.Id))).Should().BeEmpty();
        (await Save(Subjects(true, On("alibaba/wan-3.0/reference-to-video", fal), machine.Id))).Should().BeEmpty();
        (await Save(Subjects(true, On("fal-ai/not-a-model", fal), machine.Id))).Should().BeEmpty();
        (await Save(Subjects(true, ",\"model\":\"alibaba/wan-3.0/text-to-video\"", machine.Id))).Should().BeEmpty();
        Guid foreign = await AddVideoProviderAsync(InferenceProviderKind.FalAi, organizationId: Guid.NewGuid());
        (await Save(Subjects(true, On("alibaba/wan-3.0/text-to-video", foreign), machine.Id))).Should().BeEmpty();
        // A step that shows nobody is an ordinary text-to-video step.
        (await Save("{\"planSource\":\"Inline\",\"inlinePrompt\":\"x\",\"maxSpendCredits\":5" + On("alibaba/wan-3.0/text-to-video", fal) + "}"))
            .Should().BeEmpty();
    }

    [Fact]
    public async Task An_avatar_of_a_kind_that_is_switched_off_is_treated_as_absent_at_save_and_in_the_run_brief()
    {
        ProjectAvatar anna = await AddAsync(AvatarSubjectKind.Person, "Anna");
        ProjectAvatar machine = await AddAsync(AvatarSubjectKind.Object, "Machine");

        // Objects on, people off.
        AppStepFacts.UsePersonAvatarSwitch(() => false);
        AppStepFacts.UseObjectAvatarSwitch(() => true);
        (await Save(Subjects(true, "", machine.Id))).Should().BeEmpty();
        (await Save(Subjects(true, "", machine.Id, anna.Id))).Should().ContainSingle()
            .Which.Should().Contain(anna.Id.ToString()).And.EndWith("was not found in this project.");
        (await RunBriefSplitter.ProjectProblemAsync(_db, ProjectId, new RunBrief(Avatar: new RunBriefAvatar(machine.Id)), default))
            .Should().BeNull("the run brief may name an object");
        (await RunBriefSplitter.ProjectProblemAsync(_db, ProjectId, new RunBrief(Avatar: new RunBriefAvatar(anna.Id)), default))
            .Should().Be("The avatar was not found in this project.");

        // People on, objects off.
        AppStepFacts.UsePersonAvatarSwitch(() => true);
        AppStepFacts.UseObjectAvatarSwitch(() => false);
        (await Save(Subjects(true, "", anna.Id))).Should().BeEmpty();
        (await Save(Subjects(true, "", machine.Id))).Should().ContainSingle().Which.Should().EndWith("was not found in this project.");
        (await RunBriefSplitter.ProjectProblemAsync(_db, ProjectId, new RunBrief(Avatar: new RunBriefAvatar(machine.Id)), default))
            .Should().Be("The avatar was not found in this project.");
        (await RunBriefSplitter.ProjectProblemAsync(_db, ProjectId, new RunBrief(Avatar: new RunBriefAvatar(anna.Id)), default)).Should().BeNull();
    }

    // ---------------------------------------------------------------- recipes per kind

    [Fact]
    public void A_recipe_written_for_a_person_is_listed_only_while_people_are_on()
    {
        string[] personRecipes =
        [
            WorkflowTemplateCatalog.SpokespersonClipTemplateKey, WorkflowTemplateCatalog.TalkingAvatarBudgetTemplateKey,
            WorkflowTemplateCatalog.AvatarClipTemplateKey, WorkflowTemplatePresets.AvatarExplainerKey,
        ];

        // An objects-only deployment: the brief's Avatar input is offered (it may name an object), the person recipes are not.
        AppStepFacts.UsePersonAvatarSwitch(() => false);
        AppStepFacts.UseObjectAvatarSwitch(() => true);
        AppStepFacts.AvatarsEnabled.Should().BeTrue("the either-switch (the brief's Avatar input) is untouched by the per-kind ones");
        WorkflowTemplateCatalog.GetListed().Select(t => t.Key).Should().NotContain(personRecipes);
        StepCatalog.GetTemplateFacts().Select(t => t.Key).Should().NotContain(personRecipes);

        AppStepFacts.UsePersonAvatarSwitch(() => true);
        WorkflowTemplateCatalog.GetListed().Select(t => t.Key).Should().Contain(WorkflowTemplateCatalog.AvatarClipTemplateKey);
        StepCatalog.GetTemplateFacts().Select(t => t.Key).Should().Contain(WorkflowTemplateCatalog.AvatarClipTemplateKey);
    }

    [Fact]
    public void The_provisioning_service_creates_a_person_recipe_by_the_same_switch_that_lists_it()
    {
        static WorkflowTemplateProvisioningService Service(InferenceApiDbContext db, bool people, bool objects) => new(
            db, Microsoft.Extensions.Logging.Abstractions.NullLogger<WorkflowTemplateProvisioningService>.Instance,
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Avatars:Enabled"] = people ? "true" : "false",
                ["Avatars:Objects:Enabled"] = objects ? "true" : "false",
            }).Build());

        Service(_db, people: false, objects: true).AreAvatarsEnabled().Should().BeFalse("objects alone do not open a recipe written for a person");
        Service(_db, people: true, objects: false).AreAvatarsEnabled().Should().BeTrue();
    }

    // ---------------------------------------------------------------- the linter

    [Fact]
    public async Task WL015_fires_once_for_a_step_with_subjects_and_no_permission_and_WL027_judges_each_of_them()
    {
        Guid anna = Guid.NewGuid(), machine = Guid.NewGuid(), cup = Guid.NewGuid();

        IReadOnlyList<LintFinding> egressOff = await LintAsync([Of(1, StepType.VideoGenerate, Subjects(false, "", anna, machine))]);
        LintFinding egress = egressOff.Rule("WL015").Should().ContainSingle().Subject;
        egress.MessageKey.Should().Be("WL015.showsAnAvatar");
        egress.Message.Should().Contain("shows an avatar, so it sends");
        egress.Fix!.Kind.Should().Be(LintFixKind.FillInput, "the permission is the app page's own field (MediaEgressNeed)");

        var facts = new FakeFacts();
        facts.Avatars[anna] = new LintAvatarFact(true, false, "consent_revoked", "Anna presenter");
        facts.Avatars[machine] = new LintAvatarFact(true, true, null, "Espresso machine");
        facts.Avatars[cup] = new LintAvatarFact(false, false, "avatar_not_found", null);
        IReadOnlyList<LintFinding> findings = await LintAsync(
            [Of(1, StepType.VideoGenerate, Subjects(true, "", anna, machine, cup))], facts: facts);

        findings.Rule("WL015").Should().BeEmpty();
        List<LintFinding> unusable = [.. findings.Rule("WL027")];
        unusable.Should().HaveCount(2, "the usable object is not a finding; the revoked person and the absent avatar each are");
        unusable.Should().Contain(f => f.Message.Contains("\"Anna presenter\"") && f.Args!["reason"] == "consentRevoked");
        unusable.Should().Contain(f => f.Args!["reason"] == "notInProject");
    }

    [Theory]
    [InlineData(InferenceProviderKind.FalAi, "alibaba/wan-3.0/text-to-video", "alibaba/wan-3.0/image-to-video")]
    [InlineData(InferenceProviderKind.FalAi, "fal-ai/veo3.1", "fal-ai/veo3.1/reference-to-video")]
    [InlineData(InferenceProviderKind.Higgsfield, "kling-video/v3.0/std/text-to-video", "kling-video/v3.0/std/image-to-video")]
    [InlineData(InferenceProviderKind.ComfyUI, "wan2.2-ti2v-5b-t2v", "wan2.2-ti2v-5b-i2v")]
    public async Task WL034_fires_for_a_step_that_shows_a_subject_on_a_model_that_takes_no_pictures_and_names_the_sibling(
        InferenceProviderKind kind, string model, string sibling)
    {
        var facts = new FakeFacts { VideoProviderKind = kind };
        string json = Subjects(true, ",\"model\":\"" + model + "\"", Guid.NewGuid());

        LintFinding finding = (await LintAsync([Of(1, StepType.VideoGenerate, json)], facts: facts)).Rule("WL034").Should().ContainSingle().Subject;

        finding.Severity.Should().Be(LintSeverity.Error);
        finding.StepOrder.Should().Be(1);
        finding.MessageKey.Should().Be("WL034.useSibling");
        finding.Message.Should().Contain($"its model '{model}' takes no pictures").And.Contain($"Use '{sibling}' instead.");
        finding.Args!["model"].Should().Be(model);
        finding.Args!["sibling"].Should().Be(sibling);
        finding.Fix!.Kind.Should().Be(LintFixKind.ChooseModel);
        finding.Fix.LabelId.Should().Be("WL034.chooseModel");
    }

    [Fact]
    public async Task WL034_stays_quiet_when_the_model_takes_pictures_the_step_shows_nobody_or_the_provider_is_unknown()
    {
        Guid avatar = Guid.NewGuid();
        string textOnly = ",\"model\":\"alibaba/wan-3.0/text-to-video\"";
        var fal = new FakeFacts { VideoProviderKind = InferenceProviderKind.FalAi };

        // A model with a first frame, and one with its own reference mode.
        (await LintAsync([Of(1, StepType.VideoGenerate, Subjects(true, ",\"model\":\"alibaba/wan-3.0/image-to-video\"", avatar))], facts: fal))
            .Rule("WL034").Should().BeEmpty();
        (await LintAsync([Of(1, StepType.VideoGenerate, Subjects(true, ",\"model\":\"alibaba/wan-3.0/reference-to-video\"", avatar))], facts: fal))
            .Rule("WL034").Should().BeEmpty();
        (await LintAsync([Of(1, StepType.VideoGenerate, Subjects(true, "", avatar))], facts: new FakeFacts { VideoProviderKind = InferenceProviderKind.MiniMax }))
            .Rule("WL034").Should().BeEmpty("MiniMax-H3 has a reference mode");
        // No subject: a text-to-video step is an ordinary step.
        (await LintAsync([Of(1, StepType.VideoGenerate, "{\"planSource\":\"Inline\",\"inlinePrompt\":\"x\",\"maxSpendCredits\":5" + textOnly + "}")], facts: fal))
            .Rule("WL034").Should().BeEmpty();
        // An unknown provider, or a model the platform has no declaration for, is unknown: never a finding.
        (await LintAsync([Of(1, StepType.VideoGenerate, Subjects(true, textOnly, avatar))])).Rule("WL034").Should().BeEmpty();
        (await LintAsync([Of(1, StepType.VideoGenerate, Subjects(true, ",\"model\":\"fal-ai/not-a-model\"", avatar))], facts: fal))
            .Rule("WL034").Should().BeEmpty();
        // The run brief's avatar on a plain inline step is a subject too.
        string bare = "{\"planSource\":\"Inline\",\"inlinePrompt\":\"x\",\"maxSpendCredits\":5,\"allowSourceMediaEgress\":true" + textOnly + "}";
        (await LintAsync([Of(1, StepType.VideoGenerate, bare)], brief: new RunBrief(Avatar: new RunBriefAvatar(avatar)), facts: fal))
            .Rule("WL034").Should().ContainSingle();
    }

    [Fact]
    public void WL034_is_in_the_rule_table_and_on_the_VideoGenerate_card()
    {
        StepCompatibilityRule rule = StepCompatibilityRules.Get("WL034");
        (rule.Name, rule.Severity, rule.FixKind).Should().Be(("subject-model-unsupported", LintSeverity.Error, LintFixKind.ChooseModel));
        StepCompatibilityRules.AppliesTo("WL034").Should().BeEquivalentTo([StepType.VideoGenerate]);
    }

    // ---------------------------------------------------------------- the approval card

    [Fact]
    public void The_card_names_everything_shown_by_kind_and_says_what_leaves()
    {
        AssistantApprovalCardBuilder.SubjectsLine([("Anna", AvatarSubjectKind.Person)]).Should().Be(
            "Shows: “Anna” (avatar). Sends that person's pictures, videos and voice samples to the video provider.",
            "one person reads exactly as it always did");
        AssistantApprovalCardBuilder.SubjectsLine([("Espresso machine", AvatarSubjectKind.Object)]).Should().Be(
            "Shows: “Espresso machine” (object). Sends that object's pictures and videos to the video provider.");
        AssistantApprovalCardBuilder.SubjectsLine([("Anna", AvatarSubjectKind.Person), ("Espresso machine", AvatarSubjectKind.Object)]).Should().Be(
            "Shows: “Anna” (avatar), “Espresso machine” (object). Sends their pictures, videos and voice samples to the video provider.");
        AssistantApprovalCardBuilder.SubjectsLine([("Cup", AvatarSubjectKind.Object), ("Espresso machine", AvatarSubjectKind.Object)]).Should().Be(
            "Shows: “Cup” (object), “Espresso machine” (object). Sends their pictures and videos to the video provider.");
        AssistantApprovalCardBuilder.SubjectsLine([]).Should().BeNull();
    }

    [Fact]
    public void The_card_finds_every_subject_of_a_step_config()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();

        AssistantApprovalCardBuilder.AvatarIdsInConfig(Subjects(true, "", a, b)).Should().BeEquivalentTo([a, b]);
    }

    // ---------------------------------------------------------------- the assistant's egress guard

    [Fact]
    public void The_assistant_never_turns_egress_on_for_subjects_and_a_changed_subject_list_resets_the_permission()
    {
        Guid anna = Guid.NewGuid(), machine = Guid.NewGuid(), cup = Guid.NewGuid();
        Guid agent = Guid.NewGuid();
        CreateWorkflowStepRequest Req(string json) => Step(json) with { AgentDefinitionId = agent };

        // Off -> on for a step with subjects: refused (the permission is the customer's, in the app).
        AssistantEgressConsentGuard.Evaluate([Req(Subjects(true, "", anna, machine))], [Req(Subjects(false, "", anna, machine))])
            .Refusal.Should().NotBeNull();
        // Unchanged consented step: kept.
        AssistantEgressConsentGuard.Evaluate([Req(Subjects(true, "", anna, machine))], [Req(Subjects(true, "", anna, machine))])
            .Steps[0].VideoGenerateConfigJson.Should().Contain("\"allowSourceMediaEgress\":true");
        // Another subject, one subject more, or another way of showing them: the permission is reset with a notice.
        foreach (string changed in new[]
                 {
                     Subjects(true, "", anna, cup), Subjects(true, "", anna, machine, cup),
                     Subjects(true, ",\"subjectMode\":\"FirstFrame\"", anna, machine),
                 })
        {
            AssistantEgressConsentGuard.Outcome outcome =
                AssistantEgressConsentGuard.Evaluate([Req(changed)], [Req(Subjects(true, "", anna, machine))]);
            outcome.Refusal.Should().BeNull();
            AssistantEgressConsentGuard.HasConsent(outcome.Steps[0].VideoGenerateConfigJson!).Should().BeFalse(changed);
            outcome.Notes.Should().ContainSingle();
        }
    }

    // ---------------------------------------------------------------- the usage report

    [Fact]
    public async Task Usage_counts_a_take_that_showed_the_avatar_as_one_of_several_subjects()
    {
        var h = new H();
        h.Options.Objects.Enabled = true;
        Guid person = await h.CreateReadyAsync();
        Guid machine = await h.CreateObjectAsync("Machine");
        Guid execution = Guid.NewGuid();
        using (InferenceApiDbContext db = h.NewDb())
        {
            ExternalGenerationJob Take(string clip, Guid? avatarId, params Guid[] subjects) => new()
            {
                Id = Guid.NewGuid(),
                ExecutionId = execution,
                StepResultId = Guid.NewGuid(),
                ProjectId = h.Project.Id,
                ClipId = clip,
                AvatarId = avatarId,
                SubjectIdsJson = ExternalGenerationJob.WriteSubjectIds(subjects),
                Status = ExternalGenerationJobStatus.Succeeded,
                StorageKey = "k",
                CreatedAt = DateTime.UtcNow,
            };

            // avatar_id holds the person; the object is named only in subject_ids_json.
            db.ExternalGenerationJobs.Add(Take("both", person, person, machine));
            db.ExternalGenerationJobs.Add(Take("legacy-person", person));
            db.ExternalGenerationJobs.Add(Take("other-object", Guid.NewGuid(), Guid.NewGuid()));
            db.ExternalGenerationJobs.Add(Take("plain", null));
            await db.SaveChangesAsync();
        }

        (int status, JsonElement forObject) = H.Read(await h.Controller().Usage(h.Project.Id, machine, CancellationToken.None));
        status.Should().Be(200, forObject.ToString());
        forObject.GetProperty("takeCount").GetInt32().Should().Be(1);
        forObject.GetProperty("runs")[0].GetProperty("takes")[0].GetProperty("clipId").GetString().Should().Be("both");

        JsonElement forPerson = H.Read(await h.Controller().Usage(h.Project.Id, person, CancellationToken.None)).Body;
        forPerson.GetProperty("takeCount").GetInt32().Should().Be(2, "the take named twice (avatar_id and the list) counts once");
    }

    [Fact]
    public void The_subject_id_list_round_trips_and_an_unreadable_one_is_empty()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();

        string json = ExternalGenerationJob.WriteSubjectIds([a, b])!;
        json.Should().Be("[\"" + a.ToString("D") + "\",\"" + b.ToString("D") + "\"]");
        ExternalGenerationJob.ReadSubjectIds(json).Should().Equal(a, b);
        ExternalGenerationJob.WriteSubjectIds([]).Should().BeNull();
        ExternalGenerationJob.WriteSubjectIds(null).Should().BeNull();
        ExternalGenerationJob.ReadSubjectIds(null).Should().BeEmpty();
        ExternalGenerationJob.ReadSubjectIds("{not json").Should().BeEmpty();
        ExternalGenerationJob.ReadSubjectIds("{\"a\":1}").Should().BeEmpty();
    }
}
