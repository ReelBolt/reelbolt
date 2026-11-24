using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Briefs;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.Lint;
using Xunit;
using static ReelBolt.WorkflowEngine.Tests.Workflows.Lint.LintKit;

namespace ReelBolt.WorkflowEngine.Tests.Avatars;

/// <summary>
/// Avatars phase 2, lane B3: the <c>VideoGenerate</c> avatar members, their shape rules, the save-time consent check,
/// the egress rule (WL015 variant) and WL027, and the assistant guard that never turns egress on for an avatar step.
/// </summary>
public class VideoGenerateAvatarConfigTests : IDisposable
{
    private static readonly Guid ProjectId = Guid.NewGuid();
    private readonly InferenceApiDbContext _db = new(new DbContextOptionsBuilder<InferenceApiDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    public void Dispose() => _db.Dispose();

    private static VideoGenerateStepConfig Config(Action<VideoGenerateStepConfig>? tweak = null)
    {
        var c = new VideoGenerateStepConfig { MaxSpendCredits = 100m, InlinePrompt = "Anna waves", AvatarId = Guid.NewGuid(), AllowSourceMediaEgress = true };
        tweak?.Invoke(c);
        return c;
    }

    [Fact]
    public void A_config_without_an_avatar_validates_exactly_as_before()
    {
        var plain = new VideoGenerateStepConfig { MaxSpendCredits = 10m, InlinePrompt = "x" };
        plain.Validate().Should().BeEmpty();
        plain.HasAvatar.Should().BeFalse();
        JsonSerializer.Serialize(plain, VideoGenerateStepConfig.JsonOptions).Should().Contain("\"avatarId\":null");
    }

    [Fact]
    public void An_inline_avatar_step_is_valid()
    {
        Config().Validate().Should().BeEmpty();
        Config(c => c.AvatarReferenceRoles = [AvatarReferenceRole.Face, AvatarReferenceRole.Speech]).Validate().Should().BeEmpty();
    }

    [Fact]
    public void The_avatar_shape_rules_refuse_what_cannot_run()
    {
        Config(c => c.AvatarId = Guid.Empty).Validate().Should().Contain(e => e.Contains("AvatarId must be an avatar id"));
        Config(c => { c.PlanSource = VideoGeneratePlanSource.StepRef; c.Plan = new ExtractInputRef(ExtractInputSource.Previous); })
            .Validate().Should().Contain(e => e.Contains("PlanSource=Inline only"));
        Config(c => c.FirstFrameProjectFileId = Guid.NewGuid()).Validate().Should().Contain(e => e.Contains("mutually exclusive"));
        Config(c => c.AvatarReferenceRoles = []).Validate().Should().Contain(e => e.Contains("picture role"));
        Config(c => c.AvatarReferenceRoles = [AvatarReferenceRole.Speech]).Validate().Should().Contain(e => e.Contains("picture role"));
        Config(c => { c.AvatarAudio = new ExtractInputRef(ExtractInputSource.Previous); c.AvatarAudioProjectFileId = Guid.NewGuid(); })
            .Validate().Should().Contain(e => e.Contains("choose one"));
        new VideoGenerateStepConfig { MaxSpendCredits = 1m, AvatarReferenceRoles = [AvatarReferenceRole.Face] }
            .Validate().Should().Contain(e => e.Contains("only to a step with an AvatarId"));
        // Phase 3: an inline step may name its speech and leave the avatar to the run brief (the engine refuses it
        // when none arrives); a plan-sourced step may not, since the brief's avatar never applies to it.
        new VideoGenerateStepConfig { MaxSpendCredits = 1m, AvatarAudioProjectFileId = Guid.NewGuid() }
            .Validate().Should().BeEmpty();
        new VideoGenerateStepConfig { MaxSpendCredits = 1m, AvatarAudioProjectFileId = Guid.NewGuid(), FirstFrameProjectFileId = Guid.NewGuid() }
            .Validate().Should().Contain(e => e.Contains("AvatarAudio applies only to an inline step"));
    }

    [Fact]
    public void The_static_save_check_reports_the_avatar_rules_with_the_step_prefix()
    {
        string json = JsonSerializer.Serialize(Config(c => c.FirstFrameProjectFileId = Guid.NewGuid()), VideoGenerateStepConfig.JsonOptions);
        StepConfigSaveValidator.Validate([Step(json)]).Should().Contain(e => e.StartsWith("Step 1 (VideoGenerate):") && e.Contains("mutually exclusive"));
    }

    [Fact]
    public async Task Saving_a_step_that_names_an_unusable_or_foreign_avatar_is_refused()
    {
        ProjectAvatar ok = await AddAvatar();
        ProjectAvatar revoked = await AddAvatar(a => a.ConsentRevokedAt = DateTime.UtcNow.AddMinutes(-1));
        ProjectAvatar expired = await AddAvatar(a => a.RetentionUntil = DateTime.UtcNow.AddMinutes(-1));
        ProjectAvatar foreign = await AddAvatar(project: Guid.NewGuid());

        (await Both([Step(Json(ok.Id))])).Should().BeEmpty();
        (await Both([Step(Json(revoked.Id))])).Should().ContainSingle().Which.Should().Contain("consent_revoked");
        (await Both([Step(Json(expired.Id))])).Should().ContainSingle().Which.Should().Contain("retention_expired");
        (await Both([Step(Json(foreign.Id))])).Should().ContainSingle().Which.Should().Contain("was not found in this project");
    }

    [Fact]
    public async Task WL015_flags_an_avatar_step_without_the_egress_permission_and_WL027_an_unusable_avatar()
    {
        Guid avatar = Guid.NewGuid();
        IReadOnlyList<LintFinding> egressOff = await LintAsync(
            [Of(1, StepType.VideoGenerate, $$"""{"planSource":"Inline","inlinePrompt":"x","maxSpendCredits":5,"avatarId":"{{avatar}}"}""")]);
        egressOff.Rule("WL015").Should().ContainSingle().Which.MessageKey.Should().Be("WL015.showsAnAvatar");

        var facts = new FakeFacts();
        facts.Avatars[avatar] = new LintAvatarFact(true, false, "consent_revoked", "Anna presenter");
        IReadOnlyList<LintFinding> revoked = await LintAsync(
            [Of(1, StepType.VideoGenerate, $$"""{"planSource":"Inline","inlinePrompt":"x","maxSpendCredits":5,"avatarId":"{{avatar}}","allowSourceMediaEgress":true}""")],
            facts: facts);
        LintFinding finding = revoked.Rule("WL027").Should().ContainSingle().Subject;
        finding.Severity.Should().Be(LintSeverity.Error);
        finding.Message.Should().Contain("\"Anna presenter\"").And.Contain("consent was withdrawn");
        finding.Args!["reason"].Should().Be("consentRevoked");
        revoked.Rule("WL015").Should().BeEmpty();
    }

    [Fact]
    public async Task WL015_and_WL027_judge_the_avatar_the_run_brief_will_fill_in()
    {
        Guid avatar = Guid.NewGuid();
        const string bare = """{"planSource":"Inline","inlinePrompt":"x","maxSpendCredits":5}""";
        RunBrief brief = new(Avatar: new RunBriefAvatar(avatar));

        (await LintAsync([Of(1, StepType.VideoGenerate, bare)])).Rule("WL015").Should().BeEmpty("no brief, no avatar");
        IReadOnlyList<LintFinding> egress = await LintAsync([Of(1, StepType.VideoGenerate, bare)], brief: brief);
        egress.Rule("WL015").Should().ContainSingle().Which.MessageKey.Should().Be("WL015.showsAnAvatar");

        var facts = new FakeFacts();
        facts.Avatars[avatar] = new LintAvatarFact(true, false, "consent_revoked", "Anna presenter");
        IReadOnlyList<LintFinding> revoked = await LintAsync(
            [Of(1, StepType.VideoGenerate, """{"planSource":"Inline","inlinePrompt":"x","maxSpendCredits":5,"allowSourceMediaEgress":true}""")],
            brief: brief, facts: facts);
        revoked.Rule("WL027").Should().ContainSingle().Which.Message.Should().Contain("consent was withdrawn");
        revoked.Rule("WL015").Should().BeEmpty();

        // A step the brief's avatar never reaches (it starts from a frame) is judged as before.
        IReadOnlyList<LintFinding> framed = await LintAsync(
            [Of(1, StepType.VideoGenerate, $$"""{"planSource":"Inline","inlinePrompt":"x","maxSpendCredits":5,"allowSourceMediaEgress":true,"firstFrameProjectFileId":"{{Guid.NewGuid()}}"}""")],
            brief: brief, facts: facts);
        framed.Rule("WL027").Should().BeEmpty();
    }

    [Fact]
    public void The_assistant_never_turns_egress_on_for_an_avatar_and_a_new_avatar_resets_the_permission()
    {
        Guid anna = Guid.NewGuid();
        Guid bruno = Guid.NewGuid();
        string Gen(Guid avatar, bool egress) =>
            $$"""{"planSource":"Inline","inlinePrompt":"x","maxSpendCredits":5,"avatarId":"{{avatar}}","allowSourceMediaEgress":{{(egress ? "true" : "false")}}}""";
        CreateWorkflowStepRequest Req(string json) => new(Guid.NewGuid(), 1, null, "clip", StepType: "VideoGenerate", VideoGenerateConfigJson: json);
        Guid agent = Guid.NewGuid();
        CreateWorkflowStepRequest ReqA(string json) => Req(json) with { AgentDefinitionId = agent };

        // Off -> on for an avatar step: refused (the permission is the customer's, in the app).
        AssistantEgressConsentGuard.Evaluate([ReqA(Gen(anna, true))], [ReqA(Gen(anna, false))]).Refusal.Should().NotBeNull();
        // Unchanged consented step: kept.
        AssistantEgressConsentGuard.Evaluate([ReqA(Gen(anna, true))], [ReqA(Gen(anna, true))]).Steps[0].VideoGenerateConfigJson
            .Should().Contain("\"allowSourceMediaEgress\":true");
        // Pointing a consented step at a different avatar: the permission is reset with a notice.
        AssistantEgressConsentGuard.Outcome swapped = AssistantEgressConsentGuard.Evaluate([ReqA(Gen(bruno, true))], [ReqA(Gen(anna, true))]);
        swapped.Refusal.Should().BeNull();
        AssistantEgressConsentGuard.HasConsent(swapped.Steps[0].VideoGenerateConfigJson!).Should().BeFalse();
        swapped.Notes.Should().ContainSingle();
    }

    private async Task<IReadOnlyList<string>> Both(List<CreateWorkflowStepRequest> steps)
    {
        IReadOnlyList<string> a = await VideoGenerateAvatarSaveValidator.ValidateAsync(_db, ProjectId, steps, CancellationToken.None);
        IReadOnlyList<string> s = VideoGenerateAvatarSaveValidator.Validate(_db, ProjectId, steps);
        a.Should().Equal(s);
        return a;
    }

    private async Task<ProjectAvatar> AddAvatar(Action<ProjectAvatar>? tweak = null, Guid? project = null)
    {
        var avatar = new ProjectAvatar
        {
            Id = Guid.NewGuid(),
            ProjectId = project ?? ProjectId,
            Name = "Anna",
            ConsentAttestedByUserId = Guid.NewGuid(),
            ConsentAttestedAt = DateTime.UtcNow.AddDays(-1),
            ConsentStatement = ReelBolt.Shared.Avatars.AvatarConsentStatement.English,
            ConsentStatementVersion = ReelBolt.Shared.Avatars.AvatarConsentStatement.Version,
            ConsentLocale = "en",
            ConsentSubjectName = "Anna Example",
            ConsentSubjectAdult = true,
            RetentionUntil = DateTime.UtcNow.AddDays(30)
        };
        tweak?.Invoke(avatar);
        _db.ProjectAvatars.Add(avatar);
        await _db.SaveChangesAsync();
        return avatar;
    }

    private static string Json(Guid avatarId) =>
        JsonSerializer.Serialize(Config(c => c.AvatarId = avatarId), VideoGenerateStepConfig.JsonOptions);

    private static CreateWorkflowStepRequest Step(string json) =>
        new(Guid.NewGuid(), 1, null, "clip", StepType: nameof(StepType.VideoGenerate), VideoGenerateConfigJson: json);
}
