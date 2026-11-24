using System;
using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Avatars;

/// <summary>
/// The truth table of the ONE avatar read-path rule, <see cref="ProjectAvatar.IsSelectable"/> /
/// <see cref="ProjectAvatar.UnselectableReason"/> (docs/design/avatars.md "The one read-path rule"). The API's
/// <c>selectable</c> flag, the web picker and the engine resolver all call it, so this file is what keeps them in step.
/// </summary>
public class ProjectAvatarSelectabilityTests
{
    private static readonly DateTime Now = new(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc);

    private static readonly AvatarReferenceState Image = new(AvatarReferenceKind.Image, Present: true, Held: false);
    private static readonly AvatarReferenceState Video = new(AvatarReferenceKind.Video, Present: true, Held: false);
    private static readonly AvatarReferenceState Audio = new(AvatarReferenceKind.Audio, Present: true, Held: false);

    private static ProjectAvatar Ready() => new()
    {
        Id = Guid.NewGuid(),
        ProjectId = Guid.NewGuid(),
        Name = "Anna",
        ConsentAttestedByUserId = Guid.NewGuid(),
        ConsentAttestedAt = Now.AddDays(-1),
        ConsentStatement = "I confirm ...",
        ConsentStatementVersion = "avatar-consent-2026-11",
        ConsentLocale = "en",
        ConsentSubjectName = "Anna Rossi",
        ConsentBasis = AvatarConsentBasis.Self,
        ConsentSubjectAdult = true,
        RetentionUntil = Now.AddDays(364),
    };

    [Fact]
    public void A_fully_consented_avatar_with_an_image_is_selectable()
    {
        ProjectAvatar avatar = Ready();

        avatar.IsSelectable(Now, [Image, Video, Audio]).Should().BeTrue();
        avatar.UnselectableReason(Now, [Image]).Should().BeNull();
    }

    /// <summary>
    /// Each row breaks the ready avatar in one or more ways; the reason is the FIRST broken rule in the documented
    /// order consent_missing, consent_revoked, retention_expired, reference_missing, reference_held, no_image_reference.
    /// </summary>
    public static TheoryData<string, Action<ProjectAvatar>, AvatarReferenceState[], string?> Cases => new()
    {
        { "ready", _ => { }, [Image], null },
        { "ready, image held elsewhere in the list is not ignored", _ => { }, [Image, Video with { Held = true }], ProjectAvatar.ReasonReferenceHeld },
        { "no attestation time", a => a.ConsentAttestedAt = null, [Image], ProjectAvatar.ReasonConsentMissing },
        { "no attesting user", a => a.ConsentAttestedByUserId = null, [Image], ProjectAvatar.ReasonConsentMissing },
        { "empty statement", a => a.ConsentStatement = " ", [Image], ProjectAvatar.ReasonConsentMissing },
        { "subject not attested adult", a => a.ConsentSubjectAdult = false, [Image], ProjectAvatar.ReasonConsentMissing },
        { "consent missing wins over everything else", a => { a.ConsentAttestedAt = null; a.ConsentRevokedAt = Now; a.RetentionUntil = Now.AddDays(-1); }, [], ProjectAvatar.ReasonConsentMissing },
        { "revoked", a => a.ConsentRevokedAt = Now.AddMinutes(-1), [Image], ProjectAvatar.ReasonConsentRevoked },
        { "revoked wins over expired and missing media", a => { a.ConsentRevokedAt = Now; a.RetentionUntil = Now.AddDays(-1); }, [Image with { Present = false }], ProjectAvatar.ReasonConsentRevoked },
        { "retention passed", a => a.RetentionUntil = Now.AddSeconds(-1), [Image], ProjectAvatar.ReasonRetentionExpired },
        { "retention ends exactly now", a => a.RetentionUntil = Now, [Image], ProjectAvatar.ReasonRetentionExpired },
        { "expired wins over missing media", a => a.RetentionUntil = Now.AddDays(-1), [Image with { Present = false }], ProjectAvatar.ReasonRetentionExpired },
        { "a video reference is gone", _ => { }, [Image, Video with { Present = false }], ProjectAvatar.ReasonReferenceMissing },
        { "missing wins over held", _ => { }, [Image with { Held = true }, Audio with { Present = false }], ProjectAvatar.ReasonReferenceMissing },
        { "the only image is held", _ => { }, [Image with { Held = true }], ProjectAvatar.ReasonReferenceHeld },
        { "held wins over no image", _ => { }, [Video with { Held = true }], ProjectAvatar.ReasonReferenceHeld },
        { "video and audio only", _ => { }, [Video, Audio], ProjectAvatar.ReasonNoImageReference },
        { "no references at all", _ => { }, [], ProjectAvatar.ReasonNoImageReference },
        { "written permission is as good as self", a => a.ConsentBasis = AvatarConsentBasis.WrittenPermission, [Image], null },
        { "metadata-only disclosure does not affect selectability", a => a.DisclosureLabel = AvatarDisclosure.MetadataOnly, [Image], null },
        { "a linked voice is not part of the rule", a => a.ProjectVoiceId = Guid.NewGuid(), [Image], null },

        // Objects (docs/design/avatar-objects.md "The avatar selectability rule with objects"): consent_missing means
        // "no attestation of its own" (the adult flag is not read), then the reference reasons; never revoked, never expired.
        { "object: attested, one picture", MakeObject, [Image], null },
        { "object: pictures and a video", MakeObject, [Image, Image, Video], null },
        { "object: the adult flag is not read", a => { MakeObject(a); a.ConsentSubjectAdult = false; }, [Image], null },
        { "object: no attestation time", a => { MakeObject(a); a.ConsentAttestedAt = null; }, [Image], ProjectAvatar.ReasonConsentMissing },
        { "object: no attesting user", a => { MakeObject(a); a.ConsentAttestedByUserId = null; }, [Image], ProjectAvatar.ReasonConsentMissing },
        { "object: empty statement", a => { MakeObject(a); a.ConsentStatement = ""; }, [Image], ProjectAvatar.ReasonConsentMissing },
        { "object: a missing attestation wins over missing media", a => { MakeObject(a); a.ConsentStatement = " "; }, [Image with { Present = false }], ProjectAvatar.ReasonConsentMissing },
        { "object: a revoke stamp is never read", a => { MakeObject(a); a.ConsentRevokedAt = Now.AddDays(-1); a.ConsentRevokedByUserId = Guid.NewGuid(); }, [Image], null },
        { "object: a past retention date is never read", a => { MakeObject(a); a.RetentionUntil = Now.AddYears(-1); }, [Image], null },
        { "object: a picture is gone or changed", MakeObject, [Image, Image with { Present = false }], ProjectAvatar.ReasonReferenceMissing },
        { "object: missing wins over held", MakeObject, [Image with { Held = true }, Video with { Present = false }], ProjectAvatar.ReasonReferenceMissing },
        { "object: a picture is held", MakeObject, [Image with { Held = true }], ProjectAvatar.ReasonReferenceHeld },
        { "object: held wins over no picture", MakeObject, [Video with { Held = true }], ProjectAvatar.ReasonReferenceHeld },
        { "object: videos only", MakeObject, [Video], ProjectAvatar.ReasonNoImageReference },
        { "object: no references at all", MakeObject, [], ProjectAvatar.ReasonNoImageReference },
    };

    /// <summary>Turns the ready person row into an object row as the API writes one (docs/design/avatar-objects.md, the column table).</summary>
    private static void MakeObject(ProjectAvatar avatar)
    {
        avatar.SubjectKind = AvatarSubjectKind.Object;
        avatar.Name = "Espresso machine";
        avatar.ConsentStatement = ReelBolt.Shared.Avatars.AvatarObjectStatement.English;
        avatar.ConsentStatementVersion = ReelBolt.Shared.Avatars.AvatarObjectStatement.Version;
        avatar.ConsentSubjectName = string.Empty;
        avatar.ConsentBasis = AvatarConsentBasis.Self;
        avatar.ConsentSubjectAdult = false;
        avatar.RetentionUntil = ProjectAvatar.NoRetention;
        avatar.DisclosureLabel = AvatarDisclosure.MetadataOnly;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void The_rule_answers_the_first_broken_condition(
        string because, Action<ProjectAvatar> breakIt, AvatarReferenceState[] references, string? expectedReason)
    {
        ProjectAvatar avatar = Ready();
        breakIt(avatar);

        avatar.UnselectableReason(Now, references).Should().Be(expectedReason, because);
        avatar.IsSelectable(Now, references).Should().Be(expectedReason is null, because);
    }

    [Fact]
    public void A_default_constructed_avatar_is_never_selectable()
    {
        new ProjectAvatar().UnselectableReason(Now, [Image]).Should().Be(ProjectAvatar.ReasonConsentMissing);
    }

    [Fact]
    public void The_reason_list_is_the_documented_set_in_the_documented_order()
    {
        ProjectAvatar.UnselectableReasons.Should().Equal(
            "consent_missing", "consent_revoked", "retention_expired",
            "reference_missing", "reference_held", "no_image_reference");
    }

    [Fact]
    public void Every_reason_the_rule_can_return_is_in_the_list()
    {
        IEnumerable<string> returned = Cases
            .Select(row => (string?)row[3])
            .OfType<string>()
            .Distinct();

        ProjectAvatar.UnselectableReasons.Should().Contain(returned);
        returned.Should().BeEquivalentTo(ProjectAvatar.UnselectableReasons,
            "the truth table must reach every reason at least once");
    }

    [Theory]
    [InlineData("ab12", "ab12", false, true)]
    [InlineData("AB12", "ab12", false, true)]
    [InlineData("ffff", "ab12", false, false)]
    [InlineData(null, "ab12", false, false)]
    [InlineData("", "", false, false)]
    [InlineData("ab12", "ab12", true, true)]
    public void Observe_treats_changed_or_gone_bytes_as_missing(string? currentSha, string attestedSha, bool held, bool present)
    {
        ProjectAvatarReference reference = new()
        {
            Kind = AvatarReferenceKind.Image,
            ContentSha256 = attestedSha,
        };

        AvatarReferenceState state = reference.Observe(currentSha, held);

        state.Present.Should().Be(present);
        state.Held.Should().Be(held);
        state.Kind.Should().Be(AvatarReferenceKind.Image);
    }

    [Fact]
    public void A_changed_reference_makes_the_avatar_unselectable_until_re_attested()
    {
        ProjectAvatar avatar = Ready();
        ProjectAvatarReference face = new() { Kind = AvatarReferenceKind.Image, Role = AvatarReferenceRole.Face, ContentSha256 = "aa" };

        avatar.UnselectableReason(Now, [face.Observe("bb", held: false)]).Should().Be(ProjectAvatar.ReasonReferenceMissing);
        avatar.UnselectableReason(Now, [face.Observe("aa", held: false)]).Should().BeNull();
    }

    // ------------------------------------------------------------------ objects and pictures

    [Fact]
    public void A_person_stays_a_person_by_default_and_the_two_kinds_are_stored_by_these_names()
    {
        new ProjectAvatar().SubjectKind.Should().Be(AvatarSubjectKind.Person);
        Enum.GetNames<AvatarSubjectKind>().Should().Equal("Person", "Object");
        ProjectAvatar.NoRetention.Should().Be(new DateTime(9999, 12, 31, 0, 0, 0, DateTimeKind.Utc));
        ProjectAvatar.NoRetention.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public void An_object_is_never_revoked_or_expired_even_centuries_from_now()
    {
        ProjectAvatar machine = Ready();
        MakeObject(machine);

        machine.UnselectableReason(new DateTime(9999, 12, 31, 23, 59, 59, DateTimeKind.Utc), [Image]).Should().BeNull();
        machine.HasObjectAttestation.Should().BeTrue();
        machine.HasCompleteConsent.Should().BeFalse("the adult flag belongs to a person and an object never sets it");
    }

    [Theory]
    [InlineData("avatar-consent-2026-11", false)]
    [InlineData("avatar-consent-2026-12", true)]
    [InlineData("", false)]
    [InlineData("avatar-object-2026-11", false)]
    public void A_person_covers_pictures_only_under_the_statement_version_that_names_them(string version, bool expected)
    {
        ProjectAvatar anna = Ready();
        anna.ConsentStatementVersion = version;

        anna.CoversPictures.Should().Be(expected);
        ReelBolt.Shared.Avatars.AvatarConsentStatement.CoversPictures(version).Should().Be(expected);
    }

    [Fact]
    public void An_object_always_covers_pictures()
    {
        ProjectAvatar machine = Ready();
        MakeObject(machine);

        machine.CoversPictures.Should().BeTrue();
        ReelBolt.Shared.Avatars.AvatarConsentStatement.CoversPictures(null).Should().BeFalse();
    }

    /// <summary>The one role rule, every kind against every role, for both subject kinds.</summary>
    [Theory]
    [InlineData(AvatarSubjectKind.Person, AvatarReferenceKind.Image, "Face,HalfBody,FullBody")]
    [InlineData(AvatarSubjectKind.Person, AvatarReferenceKind.Video, "Motion,Speech")]
    [InlineData(AvatarSubjectKind.Person, AvatarReferenceKind.Audio, "Speech")]
    [InlineData(AvatarSubjectKind.Object, AvatarReferenceKind.Image, "Front,Angle,Detail")]
    [InlineData(AvatarSubjectKind.Object, AvatarReferenceKind.Video, "Motion")]
    [InlineData(AvatarSubjectKind.Object, AvatarReferenceKind.Audio, "")]
    public void RoleFits_is_the_documented_table(AvatarSubjectKind subject, AvatarReferenceKind kind, string allowed)
    {
        string[] expected = allowed.Length == 0 ? [] : allowed.Split(',');

        Enum.GetValues<AvatarReferenceRole>()
            .Where(role => ProjectAvatarLimits.RoleFits(subject, kind, role))
            .Select(role => role.ToString())
            .Should().BeEquivalentTo(expected);
    }

    [Fact]
    public void The_reference_roles_are_append_only_and_the_role_lists_name_each_kinds_own()
    {
        Enum.GetNames<AvatarReferenceRole>().Should().Equal(
            "Face", "HalfBody", "FullBody", "Motion", "Speech", "Front", "Angle", "Detail");
        ProjectAvatarLimits.PersonRoles.Select(r => r.ToString()).Should().Equal("Face", "HalfBody", "FullBody", "Motion", "Speech");
        ProjectAvatarLimits.ObjectRoles.Select(r => r.ToString()).Should().Equal("Front", "Angle", "Detail", "Motion");
        ProjectAvatarLimits.RolesOf(AvatarSubjectKind.Object).Should().BeSameAs(ProjectAvatarLimits.ObjectRoles);
        (ProjectAvatarLimits.MaxObjectImages, ProjectAvatarLimits.MaxObjectVideos, ProjectAvatarLimits.MaxObjectsPerProject).Should().Be((8, 3, 20));
    }

    [Fact]
    public void The_rule_refuses_a_null_reference_list()
    {
        Action act = () => Ready().UnselectableReason(Now, null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
