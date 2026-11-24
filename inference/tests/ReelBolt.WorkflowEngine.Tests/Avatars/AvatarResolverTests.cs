#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Shared.Avatars;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Storage;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Services.Avatars;
using ReelBolt.WorkflowEngine.Tests.Execution;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Avatars;

/// <summary>
/// <see cref="AvatarResolver"/> (docs/avatars.md "The engine resolver"): it answers EXACTLY what the shared rule answers
/// for the same observed references (every row of <see cref="ProjectAvatarSelectabilityTests.Cases"/> is replayed
/// through real rows), it never resolves another project's avatar, it fails closed with a code instead of falling back
/// to "no avatar", it hands out references only for a usable avatar, and it never reads the bytes of a held reference or
/// of an avatar whose consent already fails.
/// </summary>
public class AvatarResolverTests
{
    private static readonly DateTime Now = new(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid ProjectId = Guid.NewGuid();

    private sealed class FixedTime(DateTime now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(now);
    }

    private sealed class Digests : IAvatarReferenceDigestReader
    {
        public Dictionary<string, byte[]> Objects { get; } = new(StringComparer.Ordinal);
        public List<string> Reads { get; } = [];

        public Task<string?> ReadSha256Async(Guid projectId, string storageKey, CancellationToken ct)
        {
            Reads.Add(storageKey);
            return Task.FromResult(storageKey.StartsWith($"projects/{projectId}/", StringComparison.Ordinal) && Objects.TryGetValue(storageKey, out byte[]? bytes)
                ? Convert.ToHexStringLower(SHA256.HashData(bytes))
                : null);
        }
    }

    private sealed class World
    {
        private readonly string _name = $"avatar-resolver-{Guid.NewGuid()}";

        public World()
        {
            using WorkflowEngineDbContext db = NewDb();
            db.Projects.Add(new Project { Id = ProjectId, Name = "P", OwnerId = Guid.NewGuid(), CreatedAt = Now, UpdatedAt = Now });
            db.SaveChanges();
        }

        public Digests Digests { get; } = new();

        public WorkflowEngineDbContext NewDb() =>
            new(new DbContextOptionsBuilder<WorkflowEngineDbContext>().UseInMemoryDatabase(_name).Options);

        public AvatarResolver Resolver() => new(new VoiceReuseGuardTests.DbScopes(NewDb), Digests, new FixedTime(Now));

        public static ProjectAvatar Ready(Guid? projectId = null) => new()
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId ?? ProjectId,
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
            CreatedAt = Now.AddDays(-1),
        };

        /// <summary>
        /// Stores the avatar with one reference row per observed state, each backed by a real file row made to be
        /// observed that way: present = same bytes as attested, not present = the bytes changed, held = CSAM-quarantined.
        /// </summary>
        public ProjectAvatar Add(ProjectAvatar avatar, IEnumerable<AvatarReferenceState> states)
        {
            using WorkflowEngineDbContext db = NewDb();
            db.ProjectAvatars.Add(avatar);
            int ordinal = 0;
            foreach (AvatarReferenceState state in states)
            {
                byte[] attested = Encoding.UTF8.GetBytes($"ref-{ordinal}-{avatar.Id}");
                ProjectFile file = AddFile(db, avatar.ProjectId, $"ref-{ordinal}", f =>
                {
                    if (state.Held) f.CsamStatus = CsamScanStatus.Quarantined;
                });
                Digests.Objects[file.StorageKey] = state.Present ? attested : Encoding.UTF8.GetBytes("replaced");
                db.ProjectAvatarReferences.Add(new ProjectAvatarReference
                {
                    Id = Guid.NewGuid(),
                    AvatarId = avatar.Id,
                    ProjectFileId = file.Id,
                    Kind = state.Kind,
                    Role = state.Kind == AvatarReferenceKind.Image ? AvatarReferenceRole.Face : AvatarReferenceRole.Motion,
                    Ordinal = ordinal++,
                    ContentSha256 = Convert.ToHexStringLower(SHA256.HashData(attested)),
                });
            }
            db.SaveChanges();
            return avatar;
        }

        public static ProjectFile AddFile(WorkflowEngineDbContext db, Guid projectId, string name, Action<ProjectFile>? tweak = null)
        {
            ProjectFile file = new()
            {
                Id = Guid.NewGuid(),
                ProjectId = projectId,
                OriginalFileName = name,
                MimeType = "image/jpeg",
                StorageKey = $"projects/{projectId}/userFiles/{Guid.NewGuid():N}-{name}.jpg",
                CsamStatus = CsamScanStatus.Clean,
                UploadedAt = Now.AddDays(-2),
            };
            tweak?.Invoke(file);
            db.ProjectFiles.Add(file);
            return file;
        }
    }

    [Theory]
    [MemberData(nameof(ProjectAvatarSelectabilityTests.Cases), MemberType = typeof(ProjectAvatarSelectabilityTests))]
    public async Task The_resolver_answers_what_the_shared_rule_answers_for_every_row_of_its_truth_table(
        string because, Action<ProjectAvatar> breakIt, AvatarReferenceState[] references, string? expectedReason)
    {
        World world = new();
        ProjectAvatar avatar = World.Ready();
        breakIt(avatar);
        if (avatar.ConsentAttestedAt is null || avatar.ConsentAttestedByUserId is null)
        {
            // Both columns are NOT NULL: the database refuses such a row, so no resolver can ever be asked about it.
            Action store = () => world.Add(avatar, references);
            store.Should().Throw<DbUpdateException>(because);
            avatar.UnselectableReason(Now, references).Should().Be(ProjectAvatar.ReasonConsentMissing, because);
            return;
        }

        world.Add(avatar, references);

        ResolvedAvatar resolved = await world.Resolver().ResolveAsync(ProjectId, avatar.Id, CancellationToken.None);

        resolved.Reason.Should().Be(avatar.UnselectableReason(Now, references), because).And.Be(expectedReason, because);
        resolved.Usable.Should().Be(expectedReason is null, because);
        if (resolved.Usable)
            resolved.References.Should().HaveCount(references.Length, because);
        else
            resolved.References.Should().BeEmpty("an unusable avatar never hands out its media");
    }

    [Theory]
    [InlineData("revoked", ProjectAvatar.ReasonConsentRevoked)]
    [InlineData("expired", ProjectAvatar.ReasonRetentionExpired)]
    [InlineData("missing", ProjectAvatar.ReasonConsentRevoked)]
    [InlineData("fine", null)]
    public async Task A_variant_of_a_person_is_only_as_usable_as_its_original_in_the_engine_too(string original, string? expected)
    {
        World world = new();
        ProjectAvatar anna = World.Ready();
        ProjectAvatar suit = World.Ready();
        suit.Name = "Anna · Suit";
        suit.VariantOfAvatarId = anna.Id;
        suit.VariantLabel = "Suit";
        if (original == "revoked") anna.ConsentRevokedAt = Now.AddHours(-1);
        if (original == "expired") anna.RetentionUntil = Now.AddMinutes(-1);
        AvatarReferenceState[] refs = [new(AvatarReferenceKind.Image, true, false)];
        if (original != "missing") world.Add(anna, refs);
        world.Add(suit, refs);

        ResolvedAvatar resolved = await world.Resolver().ResolveAsync(ProjectId, suit.Id, CancellationToken.None);

        (resolved.Usable, resolved.Reason).Should().Be((expected is null, expected));
        // The picker's list never offers it either.
        (await world.Resolver().ListSelectableAsync(ProjectId, CancellationToken.None)).Select(x => x.Id).Contains(suit.Id).Should().Be(expected is null);
    }

    [Fact]
    public async Task A_usable_avatar_comes_with_its_references_in_order_and_its_voice_and_disclosure()
    {
        World world = new();
        Guid voiceId = Guid.NewGuid();
        ProjectAvatar avatar = World.Ready();
        avatar.ProjectVoiceId = voiceId;
        avatar.DisclosureLabel = AvatarDisclosure.MetadataOnly;
        world.Add(avatar, [new(AvatarReferenceKind.Image, true, false), new(AvatarReferenceKind.Video, true, false)]);

        ResolvedAvatar resolved = await world.Resolver().ResolveRequiredAsync(ProjectId, avatar.Id, CancellationToken.None);

        resolved.Usable.Should().BeTrue();
        resolved.Name.Should().Be("Anna");
        resolved.ProjectVoiceId.Should().Be(voiceId);
        resolved.Disclosure.Should().Be(AvatarDisclosure.MetadataOnly);
        resolved.References.Select(r => r.Kind).Should().Equal(AvatarReferenceKind.Image, AvatarReferenceKind.Video);
        resolved.References.Select(r => r.Ordinal).Should().BeInAscendingOrder();
        resolved.References.Should().OnlyContain(r => r.StorageKey.StartsWith($"projects/{ProjectId}/") && r.MimeType == "image/jpeg");
    }

    // ------------------------------------------------------------------ objects (docs/design/avatar-objects.md)

    private static ProjectAvatar ReadyObject(Guid? projectId = null)
    {
        ProjectAvatar machine = World.Ready(projectId);
        machine.SubjectKind = AvatarSubjectKind.Object;
        machine.Name = "Espresso machine";
        machine.Description = "Chrome, two groups";
        machine.ConsentStatement = AvatarObjectStatement.English;
        machine.ConsentStatementVersion = AvatarObjectStatement.Version;
        machine.ConsentSubjectName = string.Empty;
        machine.ConsentSubjectAdult = false;
        machine.RetentionUntil = ProjectAvatar.NoRetention;
        machine.DisclosureLabel = AvatarDisclosure.MetadataOnly;
        return machine;
    }

    [Fact]
    public async Task A_usable_object_resolves_with_its_kind_its_description_and_its_pictures()
    {
        World world = new();
        ProjectAvatar machine = world.Add(ReadyObject(), [new(AvatarReferenceKind.Image, true, false), new(AvatarReferenceKind.Video, true, false)]);

        ResolvedAvatar resolved = await world.Resolver().ResolveRequiredAsync(ProjectId, machine.Id, CancellationToken.None);

        resolved.Usable.Should().BeTrue();
        resolved.SubjectKind.Should().Be(AvatarSubjectKind.Object);
        resolved.Name.Should().Be("Espresso machine");
        resolved.Description.Should().Be("Chrome, two groups");
        resolved.CoversPictures.Should().BeTrue();
        resolved.Disclosure.Should().Be(AvatarDisclosure.MetadataOnly);
        resolved.ProjectVoiceId.Should().BeNull();
        resolved.References.Should().HaveCount(2);
        (await world.Resolver().ListSelectableAsync(ProjectId, CancellationToken.None)).Select(a => a.Id).Should().Equal(machine.Id);
    }

    [Fact]
    public async Task An_object_is_usable_whatever_its_revoke_and_retention_columns_hold()
    {
        World world = new();
        ProjectAvatar machine = ReadyObject();
        machine.RetentionUntil = Now.AddYears(-2);
        machine.ConsentRevokedAt = Now.AddDays(-40);
        machine.ConsentRevokedByUserId = Guid.NewGuid();
        world.Add(machine, [new(AvatarReferenceKind.Image, true, false)]);

        (await world.Resolver().ResolveAsync(ProjectId, machine.Id, CancellationToken.None)).Usable.Should().BeTrue();
    }

    [Fact]
    public async Task An_unusable_object_fails_closed_keeps_its_kind_and_hands_out_nothing()
    {
        World world = new();
        ProjectAvatar machine = world.Add(ReadyObject(), [new(AvatarReferenceKind.Image, false, false)]);

        ResolvedAvatar resolved = await world.Resolver().ResolveAsync(ProjectId, machine.Id, CancellationToken.None);

        (resolved.Usable, resolved.Reason, resolved.SubjectKind).Should().Be((false, ProjectAvatar.ReasonReferenceMissing, AvatarSubjectKind.Object));
        resolved.References.Should().BeEmpty();
        resolved.Description.Should().BeNull("an unusable avatar hands out nothing a planner could use");
        resolved.CoversPictures.Should().BeFalse();
        Func<Task> required = () => world.Resolver().ResolveRequiredAsync(ProjectId, machine.Id, CancellationToken.None);
        (await required.Should().ThrowAsync<AvatarUnusableException>()).Which.Code.Should().Be(AvatarFailureCodes.ConsentMissing);
    }

    [Fact]
    public async Task An_object_without_its_own_attestation_is_consent_missing_and_its_media_is_never_read()
    {
        World world = new();
        ProjectAvatar machine = ReadyObject();
        machine.ConsentStatement = string.Empty;
        world.Add(machine, [new(AvatarReferenceKind.Image, true, false)]);

        ResolvedAvatar resolved = await world.Resolver().ResolveAsync(ProjectId, machine.Id, CancellationToken.None);

        resolved.Reason.Should().Be(ProjectAvatar.ReasonConsentMissing);
        world.Digests.Reads.Should().BeEmpty();
    }

    [Fact]
    public async Task A_person_resolves_as_a_person_and_covers_pictures_only_under_the_current_statement()
    {
        World world = new();
        ProjectAvatar old = world.Add(World.Ready(), [new(AvatarReferenceKind.Image, true, false)]);
        ProjectAvatar current = World.Ready();
        current.Name = "Bea";
        current.ConsentStatementVersion = AvatarConsentStatement.Version;
        world.Add(current, [new(AvatarReferenceKind.Image, true, false)]);

        ResolvedAvatar first = await world.Resolver().ResolveAsync(ProjectId, old.Id, CancellationToken.None);
        ResolvedAvatar second = await world.Resolver().ResolveAsync(ProjectId, current.Id, CancellationToken.None);

        (first.SubjectKind, first.Usable, first.CoversPictures).Should().Be((AvatarSubjectKind.Person, true, false));
        (second.SubjectKind, second.Usable, second.CoversPictures).Should().Be((AvatarSubjectKind.Person, true, true));
    }

    [Fact]
    public async Task The_kind_of_an_avatar_is_read_from_its_row_alone_and_only_inside_the_project()
    {
        World world = new();
        Guid otherProject = Guid.NewGuid();
        using (WorkflowEngineDbContext db = world.NewDb())
        {
            db.Projects.Add(new Project { Id = otherProject, Name = "Other", OwnerId = Guid.NewGuid(), CreatedAt = Now, UpdatedAt = Now });
            db.SaveChanges();
        }
        ProjectAvatar machine = world.Add(ReadyObject(), [new(AvatarReferenceKind.Image, true, false)]);
        ProjectAvatar anna = world.Add(World.Ready(), [new(AvatarReferenceKind.Image, true, false)]);
        ProjectAvatar foreign = world.Add(ReadyObject(otherProject), [new(AvatarReferenceKind.Image, true, false)]);
        IAvatarResolver resolver = world.Resolver();

        (await resolver.SubjectKindAsync(ProjectId, machine.Id, CancellationToken.None)).Should().Be(AvatarSubjectKind.Object);
        (await resolver.SubjectKindAsync(ProjectId, anna.Id, CancellationToken.None)).Should().Be(AvatarSubjectKind.Person);
        (await resolver.SubjectKindAsync(ProjectId, foreign.Id, CancellationToken.None)).Should().BeNull("another project's avatar does not exist here");
        (await resolver.SubjectKindAsync(ProjectId, Guid.NewGuid(), CancellationToken.None)).Should().BeNull();
        world.Digests.Reads.Should().BeEmpty("no reference and no byte is read to learn the kind");
    }

    [Fact]
    public async Task Another_projects_avatar_is_not_found_and_its_media_is_never_read()
    {
        World world = new();
        Guid otherProject = Guid.NewGuid();
        using (WorkflowEngineDbContext db = world.NewDb())
        {
            db.Projects.Add(new Project { Id = otherProject, Name = "Other", OwnerId = Guid.NewGuid(), CreatedAt = Now, UpdatedAt = Now });
            db.SaveChanges();
        }
        ProjectAvatar foreign = world.Add(World.Ready(otherProject), [new(AvatarReferenceKind.Image, true, false)]);

        AvatarResolver resolver = world.Resolver();
        ResolvedAvatar resolved = await resolver.ResolveAsync(ProjectId, foreign.Id, CancellationToken.None);

        resolved.Usable.Should().BeFalse();
        resolved.Reason.Should().Be(ResolvedAvatar.ReasonNotFound);
        resolved.Name.Should().BeEmpty("nothing of the other project's avatar leaks, not even its name");
        resolved.References.Should().BeEmpty();
        world.Digests.Reads.Should().BeEmpty();
        (await resolver.ResolveAsync(otherProject, foreign.Id, CancellationToken.None)).Usable.Should().BeTrue("the same id is usable in its own project");
    }

    [Theory]
    [InlineData("not found", "AVATAR_NOT_FOUND", "avatar_not_found")]
    [InlineData("revoked", "AVATAR_CONSENT_REVOKED", "consent_revoked")]
    [InlineData("held", "AVATAR_CONSENT_MISSING", "reference_held")]
    [InlineData("expired", "AVATAR_CONSENT_MISSING", "retention_expired")]
    public async Task Resolving_a_required_avatar_fails_closed_with_the_code_and_reason_never_no_avatar(string because, string code, string reason)
    {
        World world = new();
        Guid id = Guid.NewGuid();
        if (because != "not found")
        {
            ProjectAvatar avatar = World.Ready();
            avatar.Id = id;
            if (because == "revoked") avatar.ConsentRevokedAt = Now.AddMinutes(-1);
            if (because == "expired") avatar.RetentionUntil = Now.AddDays(-1);
            world.Add(avatar, [new(AvatarReferenceKind.Image, true, because == "held")]);
        }

        Func<Task> act = () => world.Resolver().ResolveRequiredAsync(ProjectId, id, CancellationToken.None);

        AvatarUnusableException thrown = (await act.Should().ThrowAsync<AvatarUnusableException>()).Which;
        thrown.Code.Should().Be(code, because);
        thrown.Reason.Should().Be(reason, because);
        thrown.AvatarId.Should().Be(id);
        thrown.FailureClass.Should().Be(FailureClass.UserInput);
    }

    [Fact]
    public async Task Every_rule_reason_maps_to_a_documented_failure_code()
    {
        foreach (string reason in ProjectAvatar.UnselectableReasons.Append(ResolvedAvatar.ReasonNotFound))
            AvatarFailureCodes.For(reason).Should().BeOneOf(AvatarFailureCodes.NotFound, AvatarFailureCodes.ConsentRevoked, AvatarFailureCodes.ConsentMissing);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task An_avatar_whose_consent_fails_has_no_media_read_and_a_held_reference_is_never_read()
    {
        World world = new();
        ProjectAvatar revoked = World.Ready();
        revoked.ConsentRevokedAt = Now.AddHours(-1);
        world.Add(revoked, [new(AvatarReferenceKind.Image, true, false)]);
        ProjectAvatar held = world.Add(World.Ready(), [new(AvatarReferenceKind.Image, true, true)]);

        AvatarResolver resolver = world.Resolver();
        (await resolver.ResolveAsync(ProjectId, revoked.Id, CancellationToken.None)).Reason.Should().Be(ProjectAvatar.ReasonConsentRevoked);
        (await resolver.ResolveAsync(ProjectId, held.Id, CancellationToken.None)).Reason.Should().Be(ProjectAvatar.ReasonReferenceHeld);

        world.Digests.Reads.Should().BeEmpty();
    }

    [Fact]
    public async Task A_reference_whose_object_is_gone_is_missing()
    {
        World world = new();
        ProjectAvatar avatar = world.Add(World.Ready(), [new(AvatarReferenceKind.Image, true, false)]);
        world.Digests.Objects.Clear();

        ResolvedAvatar resolved = await world.Resolver().ResolveAsync(ProjectId, avatar.Id, CancellationToken.None);

        resolved.Reason.Should().Be(ProjectAvatar.ReasonReferenceMissing);
    }

    [Fact]
    public async Task The_resolver_re_reads_the_database_on_every_call()
    {
        World world = new();
        ProjectAvatar avatar = world.Add(World.Ready(), [new(AvatarReferenceKind.Image, true, false)]);
        AvatarResolver resolver = world.Resolver();
        (await resolver.ResolveAsync(ProjectId, avatar.Id, CancellationToken.None)).Usable.Should().BeTrue();

        using (WorkflowEngineDbContext db = world.NewDb())
        {
            ProjectAvatar row = db.ProjectAvatars.Single(a => a.Id == avatar.Id);
            row.ConsentRevokedAt = Now.AddSeconds(-1);
            db.SaveChanges();
        }

        (await resolver.ResolveAsync(ProjectId, avatar.Id, CancellationToken.None)).Reason.Should().Be(ProjectAvatar.ReasonConsentRevoked);
    }

    [Fact]
    public async Task ListSelectable_returns_only_the_usable_avatars_of_the_project()
    {
        World world = new();
        ProjectAvatar ready = world.Add(World.Ready(), [new(AvatarReferenceKind.Image, true, false)]);
        ProjectAvatar noImage = World.Ready();
        noImage.Name = "Video only";
        world.Add(noImage, [new(AvatarReferenceKind.Video, true, false)]);
        Guid otherProject = Guid.NewGuid();
        world.Add(World.Ready(otherProject), [new(AvatarReferenceKind.Image, true, false)]);

        IReadOnlyList<ResolvedAvatar> selectable = await world.Resolver().ListSelectableAsync(ProjectId, CancellationToken.None);

        selectable.Select(a => a.Id).Should().Equal(ready.Id);
    }

    // ---------------------------------------------------------------------
    // The shared observation signals (AvatarReferenceObserver.IsHeld)
    // ---------------------------------------------------------------------

    private static readonly IReadOnlySet<string> NoOwners = new HashSet<string>();

    private static ProjectFile Upload(Action<ProjectFile>? tweak = null)
    {
        ProjectFile file = new()
        {
            Id = Guid.NewGuid(),
            ProjectId = ProjectId,
            StorageKey = $"projects/{ProjectId}/userFiles/a.jpg",
            CsamStatus = CsamScanStatus.Clean,
            UploadedAt = Now.AddDays(-2),
        };
        tweak?.Invoke(file);
        return file;
    }

    [Fact]
    public void A_quarantined_file_is_held_by_either_control()
    {
        AvatarReferenceObserver.IsHeld(ProjectId, Upload(f => f.CsamStatus = CsamScanStatus.Quarantined), null, ProjectOutputHoldState.None, NoOwners).Should().BeTrue();
        AvatarReferenceObserver.IsHeld(ProjectId, Upload(f => f.ModerationStatus = FileModerationStatus.Quarantined), null, ProjectOutputHoldState.None, NoOwners).Should().BeTrue();
        AvatarReferenceObserver.IsHeld(ProjectId, Upload(f => f.ModerationStatus = FileModerationStatus.Error), null, ProjectOutputHoldState.None, NoOwners).Should().BeFalse(
            "a moderation error keeps an ordinary file usable; the attach check (lane A2) is the stricter gate");
    }

    [Fact]
    public void A_platform_made_output_from_before_the_content_quarantine_marker_is_held_as_the_engine_filter_hides_it()
    {
        ProjectFile output = Upload(f => f.OutputProducedAt = Now.AddDays(-3));

        AvatarReferenceObserver.IsHeld(ProjectId, output, contentQuarantinedAt: Now.AddDays(-1), ProjectOutputHoldState.None, NoOwners).Should().BeTrue();
        AvatarReferenceObserver.IsHeld(ProjectId, Upload(f => f.OutputProducedAt = Now), contentQuarantinedAt: Now.AddDays(-1), ProjectOutputHoldState.None, NoOwners).Should().BeFalse();
        AvatarReferenceObserver.IsHeld(ProjectId, Upload(), contentQuarantinedAt: Now.AddDays(-1), ProjectOutputHoldState.None, NoOwners).Should().BeFalse(
            "the marker only hides platform-made outputs, never the user's own uploads");
    }

    [Fact]
    public void The_csam_output_hold_holds_platform_made_files_only_and_an_unreadable_hold_holds_them_all()
    {
        string outputKey = $"projects/{ProjectId}/outputFiles/{Guid.NewGuid()}/clip.mp4";
        ProjectFile old = Upload(f => { f.StorageKey = outputKey; f.OutputProducedAt = Now.AddDays(-3); });
        ProjectFile fresh = Upload(f => { f.StorageKey = outputKey; f.OutputProducedAt = Now; });

        AvatarReferenceObserver.IsHeld(ProjectId, old, null, ProjectOutputHoldState.At(Now.AddDays(-1)), NoOwners).Should().BeTrue();
        AvatarReferenceObserver.IsHeld(ProjectId, fresh, null, ProjectOutputHoldState.At(Now.AddDays(-1)), NoOwners).Should().BeFalse();
        AvatarReferenceObserver.IsHeld(ProjectId, fresh, null, ProjectOutputHoldState.At(Now.AddDays(-1)), new HashSet<string> { outputKey }).Should().BeTrue(
            "an output whose producing step result is held is held");
        AvatarReferenceObserver.IsHeld(ProjectId, fresh, null, ProjectOutputHoldState.Failed, NoOwners).Should().BeTrue();
        AvatarReferenceObserver.IsHeld(ProjectId, Upload(), null, ProjectOutputHoldState.Failed, NoOwners).Should().BeFalse(
            "the output hold never holds a user's own upload");
    }

    [Fact]
    public async Task A_reference_whose_key_is_outside_the_project_is_observed_missing_and_not_read()
    {
        World world = new();
        ProjectAvatar avatar = world.Add(World.Ready(), [new(AvatarReferenceKind.Image, true, false)]);
        using (WorkflowEngineDbContext db = world.NewDb())
        {
            Guid fileId = db.ProjectAvatarReferences.Single(r => r.AvatarId == avatar.Id).ProjectFileId;
            ProjectFile file = db.ProjectFiles.IgnoreQueryFilters().Single(f => f.Id == fileId);
            file.StorageKey = $"projects/{Guid.NewGuid()}/userFiles/elsewhere.jpg";
            db.SaveChanges();
        }

        ResolvedAvatar resolved = await world.Resolver().ResolveAsync(ProjectId, avatar.Id, CancellationToken.None);

        resolved.Reason.Should().Be(ProjectAvatar.ReasonReferenceMissing);
        world.Digests.Reads.Should().BeEmpty();
    }
}
