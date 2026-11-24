using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Inference.Api.Services.Briefs;
using ReelBolt.Inference.Api.Services.Styles;
using ReelBolt.Shared.Briefs;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.Apps;
using ReelBolt.WorkflowEngine.Services.Briefs;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Avatars;

/// <summary>
/// Avatars phase 2, lane B5: the run brief's <c>avatar</c> field (validator, splitter, stored form, engine override,
/// cache component, consumers), the approval card's avatar lines, and the "Spokesperson clip" template.
/// </summary>
public class AvatarBriefCardTemplateTests : IDisposable
{
    private static readonly Guid ProjectId = Guid.NewGuid();
    private readonly InferenceApiDbContext _db = new(new DbContextOptionsBuilder<InferenceApiDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    public void Dispose() => _db.Dispose();

    [Fact]
    public void The_validator_refuses_an_avatar_without_a_real_id()
    {
        RunBriefValidator.Validate(new RunBrief(Avatar: new RunBriefAvatar())).Should().Contain("avatar.avatarId");
        RunBriefValidator.Validate(new RunBrief(Avatar: new RunBriefAvatar(Guid.Empty))).Should().Contain("avatar.avatarId");
        RunBriefValidator.Validate(new RunBrief(Avatar: new RunBriefAvatar(Guid.NewGuid()))).Should().BeNull();
    }

    [Fact]
    public void Unknown_fields_inside_the_avatar_are_refused_on_the_wire()
    {
        (RunBrief? ok, string? none) = RunBriefValidator.ParseJson(JsonDocument.Parse("{\"avatar\":{\"avatarId\":\"" + Guid.NewGuid() + "\"}}").RootElement);
        none.Should().BeNull();
        ok!.Avatar!.AvatarId.Should().NotBeNull();
        RunBriefValidator.ParseJson(JsonDocument.Parse("""{"avatar":{"name":"Anna"}}""").RootElement).Error.Should().NotBeNull();
    }

    [Fact]
    public async Task The_splitter_keeps_a_usable_avatar_and_refuses_a_foreign_or_revoked_one()
    {
        ProjectAvatar ok = Avatar(ProjectId);
        ProjectAvatar revoked = Avatar(ProjectId, a => a.ConsentRevokedAt = DateTime.UtcNow);
        ProjectAvatar foreign = Avatar(Guid.NewGuid());
        _db.ProjectAvatars.AddRange(ok, revoked, foreign);
        await _db.SaveChangesAsync();

        RunBriefResolution good = await Resolve(ok.Id);
        good.Error.Should().BeNull();
        good.Choices!.Brief!.Avatar!.AvatarId.Should().Be(ok.Id);
        (await Resolve(revoked.Id)).Error.Should().Contain("consent_revoked");
        (await Resolve(foreign.Id)).Error.Should().Contain("not found in this project");

        RunBriefView.Compose(RunBriefJson.Serialize(good.Choices.Brief), null, null, null)!.Avatar!.AvatarId.Should().Be(ok.Id);
    }

    [Fact]
    public void The_engine_fills_only_an_inline_step_that_names_no_avatar_and_no_frame_and_never_the_egress_flag()
    {
        Guid briefAvatar = Guid.NewGuid();
        var state = new RunBriefState(new StoredRunBrief(Avatar: new RunBriefAvatar(briefAvatar)));

        VideoGenerateStepConfig empty = RunBriefOverrides.ApplyToVideoGenerate(new VideoGenerateStepConfig(), state);
        empty.AvatarId.Should().Be(briefAvatar);
        empty.AllowSourceMediaEgress.Should().BeFalse();

        Guid own = Guid.NewGuid();
        RunBriefOverrides.ApplyToVideoGenerate(new VideoGenerateStepConfig { AvatarId = own }, state).AvatarId.Should().Be(own);
        RunBriefOverrides.ApplyToVideoGenerate(new VideoGenerateStepConfig { FirstFrameProjectFileId = Guid.NewGuid() }, state).AvatarId.Should().BeNull();
        RunBriefOverrides.ApplyToVideoGenerate(new VideoGenerateStepConfig { PlanSource = VideoGeneratePlanSource.StepRef }, state).AvatarId.Should().BeNull();
        RunBriefOverrides.ApplyToVideoGenerate(new VideoGenerateStepConfig(), null).AvatarId.Should().BeNull();

        state.CacheKeyComponent(StepType.VideoGenerate, null).Should().Contain(briefAvatar.ToString("N"));
        new RunBriefState(new StoredRunBrief(Language: "it")).CacheKeyComponent(StepType.VideoGenerate, null).Should().BeNull();
    }

    [Fact]
    public void Only_VideoGenerate_reads_the_avatar_field()
    {
        BriefFieldConsumers.FieldsReadBy(AppStepFacts.From(StepType.VideoGenerate, AgentType.VideoTransform, null)).Should().Contain("avatar");
        BriefFieldConsumers.FieldsReadBy(AppStepFacts.From(StepType.AudioGenerate, AgentType.VideoTransform, null)).Should().NotContain("avatar");
    }

    [Fact]
    public void The_card_finds_every_avatar_id_including_inside_step_configs_carried_as_strings()
    {
        Guid a = Guid.NewGuid();
        Guid b = Guid.NewGuid();
        string config = JsonSerializer.Serialize(new { avatarId = b, planSource = "Inline" });
        JsonElement args = JsonDocument.Parse(JsonSerializer.Serialize(new object[]
        {
            new { op = "set", value = new { avatarId = a } },
            new { stepOrder = 1, videoGenerateConfigJson = config }
        })).RootElement;

        AssistantApprovalCardBuilder.AvatarIdsIn(args).Should().BeEquivalentTo([a, b]);
        AssistantApprovalCardBuilder.AvatarIdsInConfig("""{"model":"x"}""").Should().BeEmpty();
        AssistantApprovalCardBuilder.AvatarIdsInConfig("not json avatarId").Should().BeEmpty();
    }

    [Fact]
    public void The_spokesperson_template_ships_inert_with_the_permission_off()
    {
        WorkflowTemplateDefinition template = WorkflowTemplateCatalog.GetAll()
            .Single(t => t.Key == WorkflowTemplateCatalog.SpokespersonClipTemplateKey);
        template.Name.Should().Be("Spokesperson clip");
        template.AutoCreateOnProject.Should().BeFalse();
        WorkflowTemplateStepDefinition step = template.Steps.Should().ContainSingle().Subject;
        step.StepType.Should().Be(StepType.VideoGenerate);

        VideoGenerateStepConfig config = JsonSerializer.Deserialize<VideoGenerateStepConfig>(step.VideoGenerateConfigJson!, VideoGenerateStepConfig.JsonOptions)!;
        config.AllowSourceMediaEgress.Should().BeFalse("only the customer turns it on, in the app");
        config.AvatarId.Should().BeNull("the avatar comes from the run brief or is set on the step");
        config.Validate().Should().BeEmpty();
        config.Validate(ReelBolt.Shared.Inference.MiniMaxVideoGenerationClient.DescribeModel(config.Model)).Should().BeEmpty();
    }

    private Task<RunBriefResolution> Resolve(Guid avatarId) =>
        RunBriefSplitter.ResolveAsync(new RunStyleResolver(_db), _db, Guid.NewGuid(), ProjectId,
            new LegacyRunInputs(null, null, null, null, null, null), new RunBrief(Avatar: new RunBriefAvatar(avatarId)), CancellationToken.None);

    private static ProjectAvatar Avatar(Guid project, Action<ProjectAvatar>? tweak = null)
    {
        var avatar = new ProjectAvatar
        {
            Id = Guid.NewGuid(),
            ProjectId = project,
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
        return avatar;
    }
}
