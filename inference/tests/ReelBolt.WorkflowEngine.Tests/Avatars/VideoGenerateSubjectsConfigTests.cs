using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Avatars;

/// <summary>
/// The subject members of <see cref="VideoGenerateStepConfig"/> (docs/design/avatar-objects.md "Subjects in a video
/// generation step"): appended, off by default, written only when set, and held to the shape rules before anything
/// else happens.
/// </summary>
public class VideoGenerateSubjectsConfigTests
{
    private static readonly Guid A = Guid.Parse("aaaaaaaa-0001-4000-8000-000000000001");
    private static readonly Guid B = Guid.Parse("aaaaaaaa-0002-4000-8000-000000000001");

    private static VideoGenerateStepConfig Parse(string json) =>
        JsonSerializer.Deserialize<VideoGenerateStepConfig>(json, VideoGenerateStepConfig.JsonOptions)!;

    private static VideoGenerateStepConfig Inline(Action<VideoGenerateStepConfig>? shape = null)
    {
        var config = new VideoGenerateStepConfig { MaxSpendCredits = 100, InlinePrompt = "a scene", AllowSourceMediaEgress = true };
        shape?.Invoke(config);
        return config;
    }

    private static VideoGenerateStepConfig Planned(Action<VideoGenerateStepConfig>? shape = null) => Inline(c =>
    {
        c.PlanSource = VideoGeneratePlanSource.StepRef;
        c.Plan = new ExtractInputRef(ExtractInputSource.Previous);
        c.InlinePrompt = null;
        shape?.Invoke(c);
    });

    [Fact]
    public void A_config_that_names_no_subject_serializes_exactly_as_it_did_before_the_members_existed()
    {
        string json = JsonSerializer.Serialize(Inline(c => c.AvatarId = A), VideoGenerateStepConfig.JsonOptions);

        foreach (string member in new[] { "subjects", "subjectMode", "plannedSubjects", "plannedSubjectIds", "effectiveSubjects", "namesSubjects" })
        {
            json.Should().NotContain("\"" + member + "\"", "a default is never written, so a stored config is byte-identical");
        }

        // The member list as it was, in its order (a new member would have to be appended AND default-ignored).
        using JsonDocument doc = JsonDocument.Parse(json);
        doc.RootElement.EnumerateObject().Select(p => p.Name).Should().Equal(
            "planSource", "inlinePrompt", "plan", "planProjectFileId", "analysis", "model", "resolution", "durationSeconds", "ratio",
            "providerId", "takes", "maxClips", "maxSpendCredits", "allowSourceMediaEgress", "pollIntervalSeconds", "timeoutSeconds",
            "loopBackBehavior", "lookMatching", "cameraContinuity", "maxGpuMinutes", "conformFps", "conformHeight", "firstFrame",
            "lastFrame", "firstFrameProjectFileId", "lastFrameProjectFileId", "avatarId", "avatarReferenceRoles", "avatarAudio",
            "avatarAudioProjectFileId", "stillProviderId");
    }

    [Fact]
    public void The_members_round_trip_by_name_when_set()
    {
        VideoGenerateStepConfig config = Inline(c =>
        {
            c.Subjects = [new VideoGenerateSubject(A, [AvatarReferenceRole.Face]), new VideoGenerateSubject(B)];
            c.SubjectMode = VideoSubjectMode.FirstFrame;
        });

        string json = JsonSerializer.Serialize(config, VideoGenerateStepConfig.JsonOptions);
        json.Should().Contain("\"subjects\":[{\"avatarId\":\"" + A.ToString("D") + "\",\"roles\":[\"Face\"]},{\"avatarId\":\"" + B.ToString("D") + "\",\"roles\":null}]");
        json.Should().Contain("\"subjectMode\":\"FirstFrame\"");

        VideoGenerateStepConfig back = Parse(json);
        back.Subjects!.Select(s => s.AvatarId).Should().Equal(A, B);
        back.Subjects![0].Roles.Should().Equal(AvatarReferenceRole.Face);
        back.SubjectMode.Should().Be(VideoSubjectMode.FirstFrame);
        back.Validate().Should().BeEmpty();

        VideoGenerateStepConfig planned = Parse(JsonSerializer.Serialize(
            Planned(c => { c.PlannedSubjects = VideoPlannedSubjects.Off; }), VideoGenerateStepConfig.JsonOptions));
        planned.PlannedSubjects.Should().Be(VideoPlannedSubjects.Off);
        planned.Validate().Should().BeEmpty();
    }

    [Fact]
    public void The_defaults_are_auto_and_nothing_named()
    {
        var config = new VideoGenerateStepConfig();

        config.Subjects.Should().BeNull();
        config.SubjectMode.Should().Be(VideoSubjectMode.Auto);
        config.PlannedSubjects.Should().Be(VideoPlannedSubjects.Auto);
        config.PlannedSubjectIds.Should().BeNull();
        config.EffectiveSubjects.Should().BeEmpty();
        config.HasAvatar.Should().BeFalse();
        config.NamesSubjects.Should().BeFalse();
        ((int)VideoSubjectMode.Auto, (int)VideoSubjectMode.References, (int)VideoSubjectMode.FirstFrame).Should().Be((0, 1, 2));
        ((int)VideoPlannedSubjects.Auto, (int)VideoPlannedSubjects.Off).Should().Be((0, 1));
    }

    [Fact]
    public void EffectiveSubjects_unifies_the_legacy_avatar_and_the_list()
    {
        VideoGenerateStepConfig legacy = Inline(c => { c.AvatarId = A; c.AvatarReferenceRoles = [AvatarReferenceRole.Face]; });
        legacy.EffectiveSubjects.Should().ContainSingle().Which.AvatarId.Should().Be(A);
        legacy.EffectiveSubjects[0].Roles.Should().Equal(AvatarReferenceRole.Face);
        legacy.HasAvatar.Should().BeTrue();
        legacy.NamesSubjects.Should().BeFalse("the one legacy avatar keeps its legacy record");

        VideoGenerateStepConfig named = Inline(c => c.Subjects = [new VideoGenerateSubject(B)]);
        named.EffectiveSubjects.Select(s => s.AvatarId).Should().Equal(B);
        named.HasAvatar.Should().BeTrue();
        named.NamesSubjects.Should().BeTrue();
    }

    public static IEnumerable<object[]> Refused()
    {
        static object[] Case(string because, VideoGenerateStepConfig config, string error) => [because, config, error];
        Guid c = Guid.NewGuid(), d = Guid.NewGuid();

        yield return Case("an empty list", Inline(x => x.Subjects = []), "Subjects must name between 1 and 3 avatars");
        yield return Case("four subjects",
            Inline(x => x.Subjects = [new(A), new(B), new(c), new(d)]), "Subjects must name between 1 and 3 avatars");
        yield return Case("the same avatar twice", Inline(x => x.Subjects = [new(A), new(A)]), "Subjects names the same avatar twice");
        yield return Case("an empty id", Inline(x => x.Subjects = [new(Guid.Empty)]), "Every entry of Subjects must carry an avatar id");
        yield return Case("both ways of naming",
            Inline(x => { x.AvatarId = A; x.Subjects = [new(B)]; }), "Subjects cannot be combined with AvatarId");
        yield return Case("a plan-sourced step", Planned(x => x.Subjects = [new(A)]), "Subjects applies to PlanSource=Inline only");
        yield return Case("a first frame",
            Inline(x => { x.Subjects = [new(A)]; x.FirstFrameProjectFileId = Guid.NewGuid(); }),
            "Subjects cannot be combined with FirstFrame/LastFrame");
        yield return Case("roles without a picture",
            Inline(x => x.Subjects = [new(A, [AvatarReferenceRole.Speech])]), "A subject's Roles must include at least one picture role");
        yield return Case("no roles at all", Inline(x => x.Subjects = [new(A, [])]), "A subject's Roles must include at least one picture role");
        yield return Case("an unknown role",
            Inline(x => x.Subjects = [new(A, [AvatarReferenceRole.Face, (AvatarReferenceRole)99])]), "A subject's Roles names an unknown role");
        yield return Case("a talking head of two",
            Inline(x => { x.Subjects = [new(A), new(B)]; x.AvatarAudioProjectFileId = Guid.NewGuid(); }),
            "AvatarAudio (a talking head) applies only to a step with exactly one subject, a person");
        yield return Case("the legacy roles next to Subjects",
            Inline(x => { x.Subjects = [new(A)]; x.AvatarReferenceRoles = [AvatarReferenceRole.Face]; }),
            "AvatarReferenceRoles applies only to a step with an AvatarId");
        yield return Case("planned subjects switched off on an inline step",
            Inline(x => x.PlannedSubjects = VideoPlannedSubjects.Off), "PlannedSubjects applies to a plan-sourced step only");
        yield return Case("an allowlist on an inline step",
            Inline(x => x.PlannedSubjectIds = [A]), "PlannedSubjectIds applies to a plan-sourced step only");
        yield return Case("an allowlist with planned subjects off",
            Planned(x => { x.PlannedSubjects = VideoPlannedSubjects.Off; x.PlannedSubjectIds = [A]; }),
            "PlannedSubjectIds cannot be combined with PlannedSubjects=Off");
        yield return Case("an empty allowlist", Planned(x => x.PlannedSubjectIds = []), "PlannedSubjectIds must list between 1 and 40 avatars");
        yield return Case("a repeated allowlist id", Planned(x => x.PlannedSubjectIds = [A, A]), "PlannedSubjectIds names the same avatar twice");
        yield return Case("an empty allowlist id", Planned(x => x.PlannedSubjectIds = [Guid.Empty]), "PlannedSubjectIds must hold avatar ids");
        yield return Case("a mode that does not exist", Inline(x => x.SubjectMode = (VideoSubjectMode)9), "SubjectMode must be Auto, References or FirstFrame");
        yield return Case("a planned word that does not exist",
            Planned(x => x.PlannedSubjects = (VideoPlannedSubjects)9), "PlannedSubjects must be Auto or Off");
    }

    [Theory]
    [MemberData(nameof(Refused))]
    public void The_shape_rules_refuse(string because, VideoGenerateStepConfig config, string error)
    {
        config.Validate().Should().Contain(e => e.StartsWith(error, StringComparison.Ordinal), because);
    }

    [Fact]
    public void What_the_rules_allow()
    {
        Inline(x => x.Subjects = [new(A), new(B), new(Guid.NewGuid())]).Validate().Should().BeEmpty("three subjects");
        Inline(x => x.Subjects = [new(A, [AvatarReferenceRole.Front, AvatarReferenceRole.Motion])]).Validate()
            .Should().BeEmpty("an object's roles are picture roles too");
        Inline(x => { x.Subjects = [new(A)]; x.AvatarAudioProjectFileId = Guid.NewGuid(); }).Validate()
            .Should().BeEmpty("one subject may be a talking head (the engine checks that it is a person)");
        Inline(x => { x.Subjects = [new(A)]; x.SubjectMode = VideoSubjectMode.References; }).Validate().Should().BeEmpty();
        Planned(x => x.PlannedSubjectIds = [A, B]).Validate().Should().BeEmpty();
        Planned().Validate().Should().BeEmpty("planned subjects are on by default and need no setting");
    }

    [Fact]
    public void The_legacy_role_subset_takes_an_objects_picture_roles()
    {
        // The gap the object foundation left: a legacy avatarId that is an object could not choose its own roles.
        Inline(x => { x.AvatarId = A; x.AvatarReferenceRoles = [AvatarReferenceRole.Front, AvatarReferenceRole.Detail]; })
            .Validate().Should().BeEmpty();
        Inline(x => { x.AvatarId = A; x.AvatarReferenceRoles = [AvatarReferenceRole.Motion]; }).Validate()
            .Should().Contain(e => e.StartsWith("AvatarReferenceRoles must include at least one picture role", StringComparison.Ordinal));
    }

    [Fact]
    public void A_config_without_subjects_keeps_the_error_list_it_always_had()
    {
        // Every earlier rule's message, in its order, with nothing appended by the subject rules.
        new VideoGenerateStepConfig { Takes = 99, MaxClips = 99, DurationSeconds = 0 }.Validate().Should().Equal(
            "MaxSpendCredits must be positive (greater than 0)",
            "Takes must be between 1 and 64 inclusive",
            "MaxClips must be between 1 and 64 inclusive",
            "DurationSeconds must be positive (greater than 0)");
    }
}
