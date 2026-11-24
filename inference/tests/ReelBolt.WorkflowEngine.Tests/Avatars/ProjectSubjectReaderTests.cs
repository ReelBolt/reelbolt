#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Avatars;
using ReelBolt.Shared.Avatars;
using ReelBolt.Shared.Data.Models;
using Xunit;
using H = ReelBolt.WorkflowEngine.Tests.Avatars.AvatarTestHarness;

namespace ReelBolt.WorkflowEngine.Tests.Avatars;

/// <summary>
/// Lane L6 of docs/design/avatar-objects.md: <see cref="IProjectSubjectReader"/>, the API's reader of the avatars and
/// objects a caller wants to show. It refuses a list it cannot place, applies the ONE selectability rule (the same
/// answer as the avatar route's <c>selectable</c>), the picture consent version, and hands back exactly the bytes whose
/// digest it checked.
/// </summary>
public class ProjectSubjectReaderTests
{
    private static H Both()
    {
        var h = new H();
        h.Options.Enabled = true;
        h.Options.Objects.Enabled = true;
        return h;
    }

    private static ProjectSubjectReader Reader(H h, InferenceApiDbContext? db = null) =>
        new(db ?? h.NewDb(), h.Storage.Object, Microsoft.Extensions.Options.Options.Create(h.Options), NullLogger<ProjectSubjectReader>.Instance);

    private static Task<ProjectSubjectRead> ReadAsync(
        H h, ProjectSubjectPurpose purpose, ProjectSubjectPictureBudget? budget, params Guid[] ids) =>
        ((IProjectSubjectReader)Reader(h)).ReadAsync(h.Project.Id, ids, purpose, budget ?? ProjectSubjectPictureBudget.Default, CancellationToken.None);

    private static (int Status, string Code, JsonElement Body) Refusal(AvatarRefusal? refusal)
    {
        refusal.Should().NotBeNull();
        (int status, JsonElement body) = H.Read(refusal!.ToResult());
        return (status, body.GetProperty("code").GetString()!, body);
    }

    // ------------------------------------------------------------------ the list

    [Fact]
    public async Task A_list_that_cannot_be_placed_is_refused_before_any_avatar_is_read()
    {
        H h = Both();
        Guid anna = await h.CreateReadyAsync("Anna");
        Guid bruno = await h.CreateReadyAsync("Bruno");
        Guid machine = await h.CreateObjectAsync("Espresso machine");
        Guid cup = await h.CreateObjectAsync("Cup");
        Guid saucer = await h.CreateObjectAsync("Saucer");
        h.Storage.Invocations.Clear();
        ProjectSubjectReader reader = Reader(h);

        (int status, string code, _) = Refusal((await reader.FindAsync(h.Project.Id, [], CancellationToken.None)).Refusal);
        (status, code).Should().Be((400, "subjects_required"));
        Refusal((await reader.FindAsync(h.Project.Id, null, CancellationToken.None)).Refusal).Code.Should().Be("subjects_required");

        (status, code, _) = Refusal((await reader.FindAsync(h.Project.Id, [anna, machine, cup, saucer], CancellationToken.None)).Refusal);
        (status, code).Should().Be((400, "too_many_subjects"));

        (status, code, _) = Refusal((await reader.FindAsync(h.Project.Id, [machine, anna, machine], CancellationToken.None)).Refusal);
        (status, code).Should().Be((400, "duplicate_subject"));

        (status, code, _) = Refusal((await reader.FindAsync(h.Project.Id, [anna, Guid.NewGuid()], CancellationToken.None)).Refusal);
        (status, code).Should().Be((404, "AVATAR_NOT_FOUND"));

        (status, code, _) = Refusal((await reader.FindAsync(h.Project.Id, [anna, bruno], CancellationToken.None)).Refusal);
        (status, code).Should().Be((400, "too_many_people"), "at most one person in one picture or clip");

        ProjectSubjectLookup found = await reader.FindAsync(h.Project.Id, [machine, anna, cup], CancellationToken.None);
        found.Refusal.Should().BeNull();
        found.Avatars.Select(a => a.Id).Should().Equal([machine, anna, cup], "the order the caller named them in");
        found.HasPerson.Should().BeTrue();
        found.Kinds.Should().BeEquivalentTo([AvatarSubjectKind.Object, AvatarSubjectKind.Person]);

        h.Storage.Invocations.Should().BeEmpty("finding the avatars reads no media");
    }

    [Fact]
    public async Task An_avatar_of_another_project_or_of_a_kind_that_is_switched_off_is_not_found()
    {
        H h = Both();
        Guid anna = await h.CreateReadyAsync("Anna");
        Guid machine = await h.CreateObjectAsync("Espresso machine");

        // another project of the same organization
        Guid otherProject = Guid.NewGuid();
        using (InferenceApiDbContext db = h.NewDb())
        {
            db.Projects.Add(new Project { Id = otherProject, Name = "Other", OwnerId = h.OwnerId, OrganizationId = h.OrganizationId });
            await db.SaveChangesAsync();
        }

        Refusal((await Reader(h).FindAsync(otherProject, [anna], CancellationToken.None)).Refusal).Code.Should().Be("AVATAR_NOT_FOUND");

        h.Options.Objects.Enabled = false;
        (int status, string code, _) = Refusal((await Reader(h).FindAsync(h.Project.Id, [anna, machine], CancellationToken.None)).Refusal);
        (status, code).Should().Be((404, "AVATAR_NOT_FOUND"), "a row of a kind whose switch is off is treated as absent");
        (await Reader(h).FindAsync(h.Project.Id, [anna], CancellationToken.None)).Refusal.Should().BeNull();

        h.Options.Enabled = false;
        Refusal((await Reader(h).FindAsync(h.Project.Id, [anna], CancellationToken.None)).Refusal).Code.Should().Be("AVATAR_NOT_FOUND");
    }

    // ------------------------------------------------------------------ usable subjects

    [Fact]
    public async Task Usable_subjects_come_back_in_order_with_their_facts_and_the_bytes_that_were_attested()
    {
        H h = Both();
        ProjectFile face = h.AddImage("anna-1.png", salt: 1);
        ProjectFile half = h.AddImage("anna-2.png", salt: 2);
        ProjectFile clip = h.AddProbedMedia("anna.mp4", "video/mp4", 5, "h264");
        Guid anna = await h.CreateReadyAsync("Anna", face, clip, half);
        ProjectFile front = h.AddImage("machine.png", salt: 3);
        Guid machine = await h.CreateObjectAsync("Espresso machine", front);
        using (InferenceApiDbContext db = h.NewDb())
        {
            (await db.ProjectAvatars.SingleAsync(a => a.Id == machine)).Description = "chrome, two groups";
            (await db.ProjectAvatars.SingleAsync(a => a.Id == anna)).DisclosureLabel = AvatarDisclosure.Visible;
            await db.SaveChangesAsync();
        }

        ProjectSubjectRead read = await ReadAsync(h, ProjectSubjectPurpose.Picture, null, machine, anna);

        read.Refusal.Should().BeNull();
        read.HasPerson.Should().BeTrue();
        read.Disclosure.Should().Be(AvatarDisclosure.Visible, "the person's own label decides");
        read.Subjects.Select(s => s.AvatarId).Should().Equal(machine, anna);

        ProjectSubject theMachine = read.Subjects[0];
        theMachine.Kind.Should().Be(AvatarSubjectKind.Object);
        theMachine.Name.Should().Be("Espresso machine");
        theMachine.Description.Should().Be("chrome, two groups");
        theMachine.Disclosure.Should().Be(AvatarDisclosure.MetadataOnly, "an object has no visible label");
        theMachine.Pictures.Should().ContainSingle().Which.Bytes.Should().Equal(h.Objects[front.StorageKey]);
        theMachine.Pictures[0].ContentType.Should().Be("image/png");
        theMachine.Pictures[0].ProjectFileId.Should().Be(front.Id);

        ProjectSubject thePerson = read.Subjects[1];
        thePerson.Kind.Should().Be(AvatarSubjectKind.Person);
        thePerson.Description.Should().BeNull();
        thePerson.PicturesAvailable.Should().Be(2, "the video is not a picture");
        thePerson.Pictures.Select(p => p.ProjectFileId).Should().Equal([face.Id, half.Id], "pictures only, in the avatar's own order");
        thePerson.Pictures[1].Bytes.Should().Equal(h.Objects[half.StorageKey]);
    }

    [Fact]
    public async Task Pictures_are_dealt_one_per_subject_at_a_time_within_the_budget()
    {
        H h = Both();
        Guid anna = await h.CreateReadyAsync("Anna", [.. Enumerable.Range(0, 5).Select(i => h.AddImage($"a{i}.png", salt: (byte)(10 + i)))]);
        Guid machine = await h.CreateObjectAsync("Machine", [.. Enumerable.Range(0, 2).Select(i => h.AddImage($"m{i}.png", salt: (byte)(20 + i)))]);
        Guid cup = await h.CreateObjectAsync("Cup", [.. Enumerable.Range(0, 4).Select(i => h.AddImage($"c{i}.png", salt: (byte)(30 + i)))]);

        ProjectSubjectRead two = await ReadAsync(h, ProjectSubjectPurpose.Picture, null, anna, machine);
        two.Subjects.Select(s => s.Pictures.Count).Should().Equal([3, 2], "3 per subject, and an avatar never sends more than it has");
        two.Subjects.Select(s => s.PicturesAvailable).Should().Equal(5, 2);

        ProjectSubjectRead three = await ReadAsync(h, ProjectSubjectPurpose.Picture, null, anna, machine, cup);
        three.Subjects.Select(s => s.Pictures.Count).Should().Equal([2, 2, 2], "6 in all, one each in turn");

        ProjectSubjectRead tight = await ReadAsync(h, ProjectSubjectPurpose.Picture, new ProjectSubjectPictureBudget(3, 4), anna, machine, cup);
        tight.Subjects.Select(s => s.Pictures.Count).Should().Equal([2, 1, 1], "every subject keeps one before any gets a second");

        ProjectSubjectRead none = await ReadAsync(h, ProjectSubjectPurpose.Picture, ProjectSubjectPictureBudget.None, anna, machine);
        none.Refusal.Should().BeNull("the rule is still applied");
        none.Subjects.Should().OnlyContain(s => s.Pictures.Count == 0);
    }

    // ------------------------------------------------------------------ the one rule

    public static TheoryData<string> States => new()
    {
        "ok", "revoked", "expired", "consent_missing", "bytes_changed", "object_gone", "quarantined", "moderation_quarantined"
    };

    /// <summary>
    /// The reader never has a rule of its own: for every state an avatar can be in, it accepts the avatar exactly when
    /// the avatar route says <c>selectable</c>, and refuses it with that route's <c>unselectableReason</c>.
    /// </summary>
    [Theory]
    [MemberData(nameof(States))]
    public async Task The_reader_agrees_with_the_avatar_routes_selectable_flag_in_every_state(string state)
    {
        H h = Both();
        ProjectFile face = h.AddImage("face.png", salt: 7);
        Guid anna = await h.CreateReadyAsync("Anna", face);
        using (InferenceApiDbContext db = h.NewDb())
        {
            ProjectAvatar row = await db.ProjectAvatars.SingleAsync(a => a.Id == anna);
            ProjectFile file = await db.ProjectFiles.SingleAsync(f => f.Id == face.Id);
            switch (state)
            {
                case "revoked": row.ConsentRevokedAt = DateTime.UtcNow; row.ConsentRevokedByUserId = h.OwnerId; break;
                case "expired": row.RetentionUntil = DateTime.UtcNow.AddDays(-1); break;
                case "consent_missing": row.ConsentSubjectAdult = false; break;
                case "bytes_changed": h.Objects[face.StorageKey] = H.Png(1024, 1024, 99); break;
                case "object_gone": h.Objects.TryRemove(face.StorageKey, out _); break;
                case "quarantined": file.CsamStatus = CsamScanStatus.Quarantined; break;
                case "moderation_quarantined": file.ModerationStatus = FileModerationStatus.Quarantined; break;
            }

            await db.SaveChangesAsync();
        }

        (_, JsonElement route) = H.Read(await h.Controller().Get(h.Project.Id, anna, CancellationToken.None));
        bool selectable = route.GetProperty("selectable").GetBoolean();
        string? reason = route.GetProperty("unselectableReason").GetString();

        ProjectSubjectRead read = await ReadAsync(h, ProjectSubjectPurpose.Video, null, anna);

        (read.Refusal is null).Should().Be(selectable);
        selectable.Should().Be(state == "ok");
        if (!selectable)
        {
            (int status, string code, JsonElement body) = Refusal(read.Refusal);
            (status, code).Should().Be((409, "AVATAR_NOT_USABLE"));
            body.GetProperty("reason").GetString().Should().Be(reason);
            body.GetProperty("avatarName").GetString().Should().Be("Anna");
            body.GetProperty("avatarId").GetGuid().Should().Be(anna);
            body.GetProperty("message").GetString().Should().Contain("\"Anna\"").And.Contain("in a clip");
        }
    }

    [Fact]
    public async Task A_picture_whose_bytes_changed_since_attestation_makes_the_whole_avatar_unusable()
    {
        H h = Both();
        ProjectFile first = h.AddImage("m1.png", salt: 1);
        ProjectFile second = h.AddImage("m2.png", salt: 2);
        Guid machine = await h.CreateObjectAsync("Machine", first, second);
        h.Objects[second.StorageKey] = H.Png(1024, 1024, 77); // swapped after the object statement was ticked

        (int status, string code, JsonElement body) = Refusal((await ReadAsync(h, ProjectSubjectPurpose.Picture, null, machine)).Refusal);

        (status, code).Should().Be((409, "AVATAR_NOT_USABLE"));
        body.GetProperty("reason").GetString().Should().Be(ProjectAvatar.ReasonReferenceMissing);
        body.GetProperty("message").GetString().Should().Contain("in a picture");
    }

    [Fact]
    public async Task A_held_file_is_never_read()
    {
        H h = Both();
        ProjectFile clean = h.AddImage("m1.png", salt: 1);
        ProjectFile held = h.AddImage("m2.png", salt: 2);
        Guid machine = await h.CreateObjectAsync("Machine", clean, held);
        using (InferenceApiDbContext db = h.NewDb())
        {
            (await db.ProjectFiles.SingleAsync(f => f.Id == held.Id)).CsamStatus = CsamScanStatus.Quarantined;
            await db.SaveChangesAsync();
        }

        h.Storage.Invocations.Clear();
        (_, string code, JsonElement body) = Refusal((await ReadAsync(h, ProjectSubjectPurpose.Picture, null, machine)).Refusal);

        code.Should().Be("AVATAR_NOT_USABLE");
        body.GetProperty("reason").GetString().Should().Be(ProjectAvatar.ReasonReferenceHeld);
        h.Storage.Verify(s => s.DownloadAsync(It.IsAny<Guid>(), held.StorageKey, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_revoked_avatar_is_refused_without_reading_any_of_its_media()
    {
        H h = Both();
        Guid anna = await h.CreateReadyAsync("Anna");
        await h.Controller().RevokeConsent(h.Project.Id, anna, CancellationToken.None);
        h.Storage.Invocations.Clear();

        (_, _, JsonElement body) = Refusal((await ReadAsync(h, ProjectSubjectPurpose.Picture, null, anna)).Refusal);

        body.GetProperty("reason").GetString().Should().Be(ProjectAvatar.ReasonConsentRevoked);
        h.Storage.Invocations.Should().BeEmpty("consent comes first in the rule");
    }

    // ------------------------------------------------------------------ the picture consent version

    [Fact]
    public async Task A_person_attested_under_the_videos_only_text_is_refused_in_a_picture_and_accepted_in_a_clip()
    {
        H h = Both();
        Guid anna = await h.CreateReadyAsync("Anna");
        Guid machine = await h.CreateObjectAsync("Machine");
        using (InferenceApiDbContext db = h.NewDb())
        {
            ProjectAvatar row = await db.ProjectAvatars.SingleAsync(a => a.Id == anna);
            row.ConsentStatement = AvatarConsentStatement.English2026_11;
            row.ConsentStatementVersion = AvatarConsentStatement.Version2026_11;
            await db.SaveChangesAsync();
        }

        (int status, string code, JsonElement body) = Refusal((await ReadAsync(h, ProjectSubjectPurpose.Picture, null, machine, anna)).Refusal);
        (status, code).Should().Be((409, "AVATAR_CONSENT_NO_PICTURES"));
        body.GetProperty("message").GetString().Should().Be("Confirm this person's consent again in the Avatars tab before using them in a picture.");
        body.GetProperty("avatarName").GetString().Should().Be("Anna");

        (await ReadAsync(h, ProjectSubjectPurpose.Video, null, machine, anna)).Refusal.Should().BeNull("every statement version covers video");
        (await ReadAsync(h, ProjectSubjectPurpose.Picture, null, machine)).Refusal.Should().BeNull("an object always covers pictures");
    }

    // ------------------------------------------------------------------ small pure parts

    [Fact]
    public void The_media_type_is_read_from_the_bytes()
    {
        ProjectSubjectReader.ContentTypeOf([0xFF, 0xD8, 0xFF, 0xE0]).Should().Be("image/jpeg");
        ProjectSubjectReader.ContentTypeOf(H.Png(4, 4)).Should().Be("image/png");
        ProjectSubjectReader.ContentTypeOf([.. "RIFF"u8, 0, 0, 0, 0, .. "WEBP"u8]).Should().Be("image/webp");
        ProjectSubjectReader.ContentTypeOf([.. "GIF89a"u8]).Should().Be("image/gif");
    }

    [Fact]
    public void Names_resolve_exactly_then_without_case_and_a_miss_lists_what_can_be_used()
    {
        List<ProjectAvatar> avatars =
        [
            new() { Id = Guid.NewGuid(), Name = "Anna", SubjectKind = AvatarSubjectKind.Person },
            new() { Id = Guid.NewGuid(), Name = "anna", SubjectKind = AvatarSubjectKind.Object },
            new() { Id = Guid.NewGuid(), Name = "Espresso machine", SubjectKind = AvatarSubjectKind.Object },
            new() { Id = Guid.NewGuid(), Name = "Old Anna", SubjectKind = AvatarSubjectKind.Person, ConsentRevokedAt = DateTime.UtcNow }
        ];

        ProjectSubjectNames.Resolve(avatars, ["Anna", " espresso MACHINE "]).Avatars!.Select(a => a.Id)
            .Should().Equal(avatars[0].Id, avatars[2].Id);
        ProjectSubjectNames.Resolve(avatars, ["ANNA"]).Problem.Should().Contain("more than one");
        ProjectSubjectNames.Resolve(avatars, ["Old Anna"]).Avatars.Should().ContainSingle("a withdrawn avatar is still named, so the caller hears why it cannot be used");

        string? miss = ProjectSubjectNames.Resolve(avatars, ["Bruno"]).Problem;
        miss.Should().Contain("no avatar or object named \"Bruno\"")
            .And.Contain("\"Anna\" (person)").And.Contain("\"Espresso machine\" (object)")
            .And.NotContain("Old Anna", "only live names are offered");
        ProjectSubjectNames.Resolve(avatars, []).Problem.Should().NotBeNull();
    }
}
